using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Starward.Features.Background;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.Overlay;
using Starward.Features.Screenshot;
using Starward.Frameworks;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics;


namespace Starward.Features.ViewHost;

/// <summary>
/// 应用主窗口。负责标题栏自绘、窗口生命周期（显示/隐藏/关闭）、全局热键与系统消息分发。
/// </summary>
[INotifyPropertyChanged]
public sealed partial class MainWindow : WindowEx
{


    /// <summary>
    /// 当前主窗口单例，在构造函数中赋值。
    /// </summary>
    public static new MainWindow Current { get; private set; }


    /// <summary>
    /// 初始化主窗口：注册消息订阅、配置标题栏与窗口行为，并确保系统托盘可用。
    /// </summary>
    public MainWindow()
    {
        Current = this;
        MainWindowId = AppWindow.Id;
        this.InitializeComponent();
        InitializeMainWindow();
        App.Current.EnsureSystemTray();
        WeakReferenceMessenger.Default.Register<AccentColorChangedMessage>(this, OnAccentColorChanged);
        WeakReferenceMessenger.Default.Register<GameStartedMessage>(this, OnGameStarted);
    }



    /// <summary>
    /// 上一次用于计算标题栏按钮透传区的 DPI 缩放；跨显示器时与当前值比对以触发重算。
    /// </summary>
    private double _lastCaptionRasterizationScale;


    /// <summary>
    /// 自绘标题栏按钮占用的逻辑宽度（最小化 + 关闭，各 48 DIP）。
    /// </summary>
    internal const double CaptionButtonsWidthDip = 96;


    /// <summary>
    /// 配置窗口外观与行为：标题栏延伸、固定尺寸、拖动区域、会话通知注册等。
    /// </summary>
    private void InitializeMainWindow()
    {
        Title = "Moonward";
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Closing += AppWindow_Closing;
        Content.KeyDown += Content_KeyDown;
        CenterInScreen(1200, 676);
        HideSystemCaptionButtons();
        // 承载系统按钮的子窗口在窗口首次激活后才创建（且隐藏到托盘再显示时可能重建），
        // 故在每次激活时销毁它（幂等、自愈）
        Activated += MainWindow_Activated;
        // 销毁标题栏子窗口之后（只是隐藏），行为、消息仍然可以触发（并且优先级高于客户区），需要设置透传区域让点击落到自绘按钮上
        StackPanel_WindowCaption.Loaded += StackPanel_WindowCaption_Loaded;
        StackPanel_WindowCaption.SizeChanged += StackPanel_WindowCaption_SizeChanged;
        // 跨显示器 DPI 变化时物理像素坐标会变，但按钮 DIP 尺寸不变，SizeChanged 往往不触发；
        // 订阅 XamlRoot / AppWindow 以重算 Passthrough，否则关闭按钮会被拖动区吞掉
        AppWindow.Changed += AppWindow_Changed_RefreshCaptionHitTest;
        // 排除右上角自绘按钮区域（两个 48px 按钮），使其可点击而非作为拖动区
        SetDragRectangles(new RectInt32(0, 0, AppWindow.Size.Width - (int)(CaptionButtonsWidthDip * UIScale), (int)(48 * UIScale)));
        SetIcon();
        WTSRegisterSessionNotification(WindowHandle, 0);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
    }



    /// <summary>
    /// 隐藏系统标题栏的最小化/最大化/关闭按钮，改用右上角 XAML 自绘按钮。
    /// 仅靠 <c>AppWindow.TitleBar.Button*Color</c> 改不动这些按钮的可见性 —— WinUI3 /
    /// Windows App SDK 用一个名为 "ReunionWindowingCaptionControls" 的子窗口绘制它们，
    /// 真正的隐藏靠 <see cref="DestroyCaptionControls"/> 销毁该子窗口；此处仅把背景置透明，
    /// 避免销毁前的一瞬间露出按钮底色。配合 <see cref="SetCaptionButtonPassthroughRegions"/>
    /// 将自绘按钮区域标记为透传，使点击落到 XAML 控件上。
    /// </summary>
    private void HideSystemCaptionButtons()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }



    /// <summary>
    /// 窗口激活时销毁系统标题栏按钮子窗口，确保自绘按钮区域可交互。
    /// </summary>
    /// <param name="sender">事件源（<see cref="MainWindow"/>）。</param>
    /// <param name="args">激活事件参数，包含 <see cref="WindowActivatedEventArgs.WindowActivationState"/>。</param>
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DestroyCaptionControls();
        SetCaptionButtonPassthroughRegions();
    }



    /// <summary>
    /// 标题栏按钮容器加载完成后，订阅 DPI 变化并设置右上角按钮的透传区域。
    /// </summary>
    private void StackPanel_WindowCaption_Loaded(object sender, RoutedEventArgs e)
    {
        if (StackPanel_WindowCaption.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed -= XamlRoot_Changed_RefreshCaptionHitTest;
            xamlRoot.Changed += XamlRoot_Changed_RefreshCaptionHitTest;
            _lastCaptionRasterizationScale = xamlRoot.RasterizationScale;
        }
        RefreshCaptionButtonHitTest();
    }



    /// <summary>
    /// 标题栏按钮容器尺寸变化时，重新计算透传区域。
    /// </summary>
    private void StackPanel_WindowCaption_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetCaptionButtonPassthroughRegions();
    }



    /// <summary>
    /// XamlRoot 变化（典型为跨显示器 DPI/RasterizationScale 改变）时，延迟重算标题栏按钮命中区。
    /// 布局可能尚未按新 DPI 完成，故投递到队列下一拍再刷新。
    /// </summary>
    /// <param name="sender">发生变化的 <see cref="XamlRoot"/>。</param>
    /// <param name="args">变化参数（本方法主要比较 <see cref="XamlRoot.RasterizationScale"/>）。</param>
    private void XamlRoot_Changed_RefreshCaptionHitTest(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_lastCaptionRasterizationScale == sender.RasterizationScale)
        {
            return;
        }
        _lastCaptionRasterizationScale = sender.RasterizationScale;
        // DPI 变更后一帧内 TransformToVisual / Actual* 可能仍是旧值，延后到布局更新后再取矩形
        DispatcherQueue.TryEnqueue(RefreshCaptionButtonHitTest);
    }



    /// <summary>
    /// 窗口尺寸变化时重算透传区（跨 DPI 显示器移动时常伴随物理像素尺寸变化）。
    /// </summary>
    /// <param name="sender">事件源（<see cref="AppWindow"/>）。</param>
    /// <param name="args">变化参数；仅在 <see cref="AppWindowChangedEventArgs.DidSizeChange"/> 为 true 时处理。</param>
    private void AppWindow_Changed_RefreshCaptionHitTest(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(RefreshCaptionButtonHitTest);
    }



    /// <summary>
    /// 销毁可能重建的系统标题栏按钮子窗口，并按当前布局重算自绘按钮透传区。
    /// </summary>
    private void RefreshCaptionButtonHitTest()
    {
        DestroyCaptionControls();
        SetCaptionButtonPassthroughRegions();
    }



    /// <summary>
    /// 将右上角自绘最小化/关闭按钮区域设为透传，使非客户区输入落到 XAML 按钮上。
    /// 矩形为相对窗口的物理像素；跨显示器后必须用最新 <see cref="XamlRoot.RasterizationScale"/> 重算。
    /// </summary>
    private void SetCaptionButtonPassthroughRegions()
    {
        try
        {
            if (AppWindow.TitleBar.ExtendsContentIntoTitleBar is not true)
            {
                return;
            }
            if (StackPanel_WindowCaption.XamlRoot is null)
            {
                return;
            }
            // 布局未完成时 ActualWidth 为 0，写入空/零矩形会破坏命中，跳过待下次刷新
            if (Button_Minimize.ActualWidth <= 0 || Button_CloseWindow.ActualWidth <= 0)
            {
                return;
            }

            double scale = StackPanel_WindowCaption.XamlRoot.RasterizationScale;
            _lastCaptionRasterizationScale = scale;
            RectInt32[] rects =
            [
                GetElementRectInt32(Button_Minimize, scale),
                GetElementRectInt32(Button_CloseWindow, scale),
            ];

            InputNonClientPointerSource nonClientInputSource =
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClientInputSource.SetRegionRects(NonClientRegionKind.Passthrough, rects);
        }
        catch
        {
            // 窗口销毁或 XamlRoot 尚未就绪时忽略
        }
    }



    /// <summary>
    /// 将 XAML 元素边界转换为物理像素矩形（供 <see cref="SetCaptionButtonPassthroughRegions"/> 使用）。
    /// </summary>
    private static RectInt32 GetElementRectInt32(FrameworkElement element, double scale)
    {
        GeneralTransform transform = element.TransformToVisual(null);
        Rect bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }



    /// <summary>
    /// 销毁承载系统最小化/最大化/关闭按钮的子窗口（"ReunionWindowingCaptionControls"），
    /// 从而彻底隐藏系统按钮。该子窗口属于 UI 线程，
    /// 故必须在 UI 线程调用 DestroyWindow（Activated 事件即在 UI 线程）。
    /// </summary>
    private void DestroyCaptionControls()
    {
        try
        {
            HWND controls = User32.FindWindowEx(WindowHandle, IntPtr.Zero, "ReunionWindowingCaptionControls", "ReunionCaptionControlsWindow");
            if (!controls.IsNull)
            {
                User32.DestroyWindow(controls);
            }
        }
        catch { }
    }



    /// <summary>
    /// 显示主窗口；若当前尺寸偏离默认 1200×676（按 UI 缩放），则重新居中。
    /// 从系统托盘恢复时额外广播 <see cref="MainWindowShownMessage"/>。
    /// </summary>
    public override void Show()
    {
        double uiScale = UIScale;
        if (Math.Abs(AppWindow.Size.Width - 1200 * uiScale) > 10 || Math.Abs(AppWindow.Size.Height - 676 * uiScale) > 10)
        {
            CenterInScreen(1200, 676);
        }
        base.Show();
        NotifyShownFromHidden();
    }



    /// <summary>
    /// 由手柄导航唤起主窗口：重置尺寸并居中，同时将鼠标光标移至窗口中心。
    /// </summary>
    public void ShowByGamepad()
    {
        CenterInScreen(1200, 676);
        User32.SetCursorPos(AppWindow.Position.X + AppWindow.Size.Width / 2, AppWindow.Position.Y + AppWindow.Size.Height / 2);
        base.Show();
        NotifyShownFromHidden();
    }



    /// <summary>
    /// 是否已 <see cref="Hide"/> 到系统托盘。
    /// <para>
    /// 不能改用窗口可见性判断：<see cref="App.EnsureMainWindow"/> 先 Activate 后 Show，而 Activate
    /// 本身就会把隐藏的窗口重新显示出来，轮到 Show 时窗口早已可见，「从托盘恢复」永远判不出来。
    /// 最小化不会置位（只有 <see cref="Hide"/> 会），所以最小化恢复不算重新打开主界面。
    /// </para>
    /// </summary>
    private bool _hiddenToTray;


    /// <summary>
    /// 主窗口从隐藏状态（系统托盘）重新显示时广播 <see cref="MainWindowShownMessage"/>，
    /// 把「重新打开主界面」与普通窗口激活区分开（随机模式据此换一张背景壁纸）。
    /// </summary>
    private void NotifyShownFromHidden()
    {
        if (_hiddenToTray)
        {
            _hiddenToTray = false;
            WeakReferenceMessenger.Default.Send(new MainWindowShownMessage());
        }
    }



    /// <summary>
    /// 游戏启动后根据用户配置隐藏或最小化主窗口。
    /// </summary>
    /// <param name="_">消息接收者（本窗口实例，未使用）。</param>
    /// <param name="__"><see cref="GameStartedMessage"/> 消息体（未使用）。</param>
    private void OnGameStarted(object _, GameStartedMessage __)
    {
        StartGameAction action = AppConfig.StartGameAction;
        if (action is StartGameAction.Hide)
        {
            this.Hide();
        }
        else if (action is StartGameAction.Minimize)
        {
            this.Minimize();
        }
    }



    /// <summary>
    /// 拦截系统关闭请求（标题栏关闭、Alt+F4 等），统一走自定义关闭逻辑。
    /// </summary>
    /// <param name="sender">事件源（<see cref="AppWindow"/>）。</param>
    /// <param name="args">关闭事件参数；本方法将 <see cref="AppWindowClosingEventArgs.Cancel"/> 置为 <see langword="true"/> 以阻止默认关闭。</param>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 取消系统关闭，统一走自定义关闭逻辑（Alt+F4 / 系统菜单也经此处）
        args.Cancel = true;
        await HandleCloseRequestAsync();
    }



    /// <summary>
    /// 自绘最小化按钮点击：将窗口最小化到任务栏。
    /// </summary>
    /// <param name="sender">事件源（最小化按钮）。</param>
    /// <param name="e">路由事件参数。</param>
    private void Button_Minimize_Click(object sender, RoutedEventArgs e)
    {
        Minimize();
    }



    /// <summary>
    /// 自绘关闭按钮点击：直接调用共享关闭逻辑（不经过 <see cref="AppWindow.Closing"/>）。
    /// </summary>
    /// <param name="sender">事件源（关闭按钮）。</param>
    /// <param name="e">路由事件参数。</param>
    private async void Button_CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        // 自绘关闭按钮：Window.Close() 不会再触发 AppWindow.Closing，故直接调用共享关闭逻辑
        await HandleCloseRequestAsync();
    }



    /// <summary>
    /// 处理关闭请求：按 <see cref="AppConfig.CloseWindowOption"/> 隐藏到托盘或退出应用。
    /// 若用户尚未固定选项，则弹出对话框供其选择并持久化。
    /// </summary>
    /// <returns>表示异步关闭流程的任务。</returns>
    private async Task HandleCloseRequestAsync()
    {
        try
        {
            MainWindowCloseOption option = AppConfig.CloseWindowOption;
            // 未配置固定选项时弹出对话框
            if (option is not MainWindowCloseOption.Hide and not MainWindowCloseOption.Exit)
            {
                var dialog = new MainWindowCloseDialog
                {
                    Title = Lang.ExperienceSettingPage_CloseWindowOption,
                    PrimaryButtonText = Lang.Common_Confirm,
                    CloseButtonText = Lang.Common_Cancel,
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                var result = await dialog.ShowAsync();
                if (result is not ContentDialogResult.Primary)
                {
                    return;
                }
                option = dialog.MainWindowCloseOption.Value;
                AppConfig.CloseWindowOption = option;
            }
            if (option is MainWindowCloseOption.Hide)
            {
                Hide();
            }
            if (option is MainWindowCloseOption.Exit)
            {
                Close();
                AppInstance.GetCurrent().UnregisterKey();
                // 退出前尝试备份数据库，最多等待 30 秒
                Task backupTask = Task.Run(DatabaseService.AutoBackupToAppDataLocal);
                Task timeTask = Task.Delay(30000);
                await Task.WhenAny(backupTask, timeTask);
                App.Current.Exit();
            }
        }
        catch { }
    }



    /// <summary>
    /// 强调色变更时强制刷新内容区主题，使 Accent 相关资源重新解析。
    /// </summary>
    /// <param name="_">消息接收者（本窗口实例，未使用）。</param>
    /// <param name="__"><see cref="AccentColorChangedMessage"/> 消息体（未使用）。</param>
    private void OnAccentColorChanged(object _, AccentColorChangedMessage __)
    {
        FrameworkElement ele = (FrameworkElement)Content;
        // 先切换到相反主题再恢复 Default，触发资源重载
        ele.RequestedTheme = ele.ActualTheme switch
        {
            ElementTheme.Light => ElementTheme.Dark,
            ElementTheme.Dark => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        ele.RequestedTheme = ElementTheme.Default;
    }



    /// <summary>
    /// 内容区按键处理：按 Esc 隐藏主窗口（与关闭到托盘行为一致）。
    /// </summary>
    /// <param name="sender">事件源（窗口 <see cref="Content"/>）。</param>
    /// <param name="e">按键路由事件参数，包含 <see cref="Microsoft.UI.Xaml.Input.KeyRoutedEventArgs.Key"/>。</param>
    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Escape)
        {
            Hide();
        }
    }



    /// <summary>
    /// 隐藏主窗口并广播状态变化消息，触发背景资源释放与 GC。
    /// </summary>
    public override void Hide()
    {
        _hiddenToTray = true;
        base.Hide();
        WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true, CurrentTime = DateTimeOffset.Now });
        GC.Collect();
    }



    /// <summary>
    /// 上次窗口激活时间，用于计算激活间隔与跨小时判断。
    /// </summary>
    private DateTimeOffset _lastActivatedTime = DateTimeOffset.Now;



    /// <summary>
    /// 窗口子类过程：处理非客户区命中测试、激活/锁屏/最小化、热键与可移动存储设备变更等系统消息。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="uMsg">Windows 消息 ID。</param>
    /// <param name="wParam">消息附加参数（含义随 <paramref name="uMsg"/> 变化）。</param>
    /// <param name="lParam">消息附加参数（含义随 <paramref name="uMsg"/> 变化）。</param>
    /// <param name="uIdSubclass">子类 ID（由基类注册时分配）。</param>
    /// <param name="dwRefData">子类引用数据（由基类传入）。</param>
    /// <returns>消息处理结果；返回 0 表示已消费该消息，否则交由基类默认处理。</returns>
    protected override nint WindowSubclassProc(HWND hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == 0x0084 /* WM_NCHITTEST */)
        {
            // 取系统命中结果后重映射为客户区，让点击落到右上角自绘按钮上。
            nint result = base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
            // 8/9/20 = HTMINBUTTON/HTMAXBUTTON/HTCLOSE：屏蔽系统标题栏按钮命中。
            // 注意不动 HTCAPTION(2)，标题栏拖动区仍由 SetDragRectangles 保留。
            if (result is  9 or 20)
            {
                return 1; // 客户区
            }
            else if (result is 8)
            {
                return 2;//标题栏 可拖拽
            }
            return result;
        }
        if (uMsg == (uint)User32.WindowMessage.WM_ACTIVATE || uMsg == (uint)User32.WindowMessage.WM_POINTERACTIVATE)
        {
            // 窗口激活
            if (wParam is 0x1 or 0x2)
            {
                // WA_ACTIVE or WA_CLICKACTIVE
                var now = DateTimeOffset.Now;
                WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage
                {
                    Activate = true,
                    CurrentTime = now,
                    LastActivatedTime = _lastActivatedTime,
                });
                _lastActivatedTime = now;
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_SYSCOMMAND)
        {
            if (wParam == 0xF030)
            {
                // SC_MAXIMIZE
                // 防止双击标题栏使窗口最大化，WinAppSDK 某个版本的 Bug
                return IntPtr.Zero;
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_WTSSESSION_CHANGE)
        {
            if (wParam == 0x7)
            {
                // WTS_SESSION_LOCK
                // 锁屏，暂停视频背景
                WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { SessionLock = true, CurrentTime = DateTimeOffset.Now });
            }
            else if (wParam == 0x8)
            {
                // WTS_SESSION_UNLOCK 
            }
        }
        else if (uMsg == 0x0005 /* WM_SIZE */)
        {
            if (wParam == 1 /* SIZE_MINIMIZED */)
            {
                // 窗口最小化，通知暂停/释放背景视频资源
                WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true, CurrentTime = DateTimeOffset.Now });
                GC.Collect();
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_DEVICECHANGE)
        {
            // 存储设备插入/拔出
            if (wParam == 0x8000)
            {
                // DBT_DEVICEARRIVAL
                User32.DEV_BROADCAST_HDR dev = Marshal.PtrToStructure<User32.DEV_BROADCAST_HDR>(lParam);
                if (dev.dbch_devicetype is User32.DBT_DEVTYPE.DBT_DEVTYP_VOLUME)
                {
                    WeakReferenceMessenger.Default.Send(new RemovableStorageDeviceChangedMessage());
                }
            }
            else if (wParam == 0x8004)
            {
                // DBT_DEVICEREMOVECOMPLETE
                User32.DEV_BROADCAST_HDR dev = Marshal.PtrToStructure<User32.DEV_BROADCAST_HDR>(lParam);
                if (dev.dbch_devicetype is User32.DBT_DEVTYPE.DBT_DEVTYP_VOLUME)
                {
                    WeakReferenceMessenger.Default.Send(new RemovableStorageDeviceChangedMessage());
                }
            }
        }
        // 全局热键（WM_HOTKEY）不在这里处理：热键注册在系统托盘窗口上，见 SystemTrayWindow.WindowSubclassProc。
        // 主窗口可能压根没创建（仅托盘驻留 / 快捷方式启动），挂在这里会让热键整体失效。
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }



    /// <summary>
    /// 注册窗口以接收终端服务会话变更通知（锁屏/解锁）。
    /// </summary>
    /// <param name="hWnd">要接收通知的窗口句柄。</param>
    /// <param name="dwFlags">通知标志；0 表示仅接收当前会话的通知。</param>
    /// <returns>注册成功返回 <see langword="true"/>，否则返回 <see langword="false"/>。</returns>
    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);


}