using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Starward.Features.GameRecord.SignIn;
using Starward.Features.Overlay;
using Starward.Features.Screenshot;
using Starward.Features.Setting;
using Starward.Features.Startup;
using Starward.Frameworks;
using Starward.Helpers;
using System;
using Vanara.PInvoke;
using Windows.Foundation;


namespace Starward.Features.ViewHost;

/// <summary>
/// 系统托盘窗口。除了托盘图标与右键菜单，它还是**常驻实例的宿主**：
/// 全局热键注册在本窗口上、<c>WM_HOTKEY</c> 由本窗口分发，快捷方式启动游戏的跨进程通知也由此接收。
/// <para>
/// 选它而不是主窗口，是因为它是常驻实例中唯一必然存在（<see cref="App.EnsureMainWindow"/> 与
/// <see cref="App.EnsureSystemTray"/> 两条路径都会创建）且永不销毁（<c>AppWindow.Closing</c> 恒取消）的窗口。
/// 挂在主窗口上会导致仅托盘驻留或快捷方式启动时热键完全缺席，见 issue #10。
/// </para>
/// </summary>
[INotifyPropertyChanged]
public sealed partial class SystemTrayWindow : WindowEx
{




    public SystemTrayWindow()
    {
        this.InitializeComponent();
        InitializeWindow();
        SetTrayIcon();
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.Bindings.Update());
        // 常驻实例的对外通道与后台职责：先开 IPC 监听（快捷方式进程要靠它找到本实例），再拉起热键/手柄等
        ResidentInstanceMessenger.StartListening();
        ResidentHost.Start(WindowHandle, DispatcherQueue);
    }




    private unsafe void InitializeWindow()
    {
        new SystemBackdropHelper(this, SystemBackdropProperty.AcrylicDefault with
        {
            TintColorLight = 0xFFE7E7E7,
            TintColorDark = 0xFF404040
        }).TrySetAcrylic(true);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (s, e) => e.Cancel = true;
        this.Activated += SystemTrayWindow_Activated;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var flag = User32.GetWindowLongPtr(WindowHandle, User32.WindowLongFlags.GWL_STYLE);
        flag &= ~(nint)User32.WindowStyles.WS_CAPTION;
        flag &= ~(nint)User32.WindowStyles.WS_BORDER;
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE, flag);
        var p = DwmApi.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        DwmApi.DwmSetWindowAttribute(WindowHandle, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, (nint)(&p), sizeof(DwmApi.DWM_WINDOW_CORNER_PREFERENCE));

        Show();
        Hide();
    }



    private void SetTrayIcon()
    {
        try
        {
            nint hInstance = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint hIcon = User32.LoadIcon(hInstance, "#32512").DangerousGetHandle();
            trayIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
        }
        catch { }
    }




    private void SystemTrayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState is WindowActivationState.Deactivated)
        {
            Hide();
        }
    }



    [RelayCommand]
    public override void Show()
    {
        // 设置页改键/删键不会通知托盘，每次弹出时兜底重读一遍
        RefreshHotkeyStates();
        RootGrid.RequestedTheme = ShouldSystemUseDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SIZE windowSize = new()
        {
            Width = (int)(RootGrid.DesiredSize.Width * UIScale),
            Height = (int)(RootGrid.DesiredSize.Height * UIScale)
        };
        User32.GetCursorPos(out POINT point);
        User32.CalculatePopupWindowPosition(point, windowSize, User32.TrackPopupMenuFlags.TPM_RIGHTALIGN | User32.TrackPopupMenuFlags.TPM_BOTTOMALIGN | User32.TrackPopupMenuFlags.TPM_WORKAREA, null, out RECT windowPos);
        User32.MoveWindow(WindowHandle, windowPos.X, windowPos.Y, windowPos.Width, windowPos.Height, true);
        base.Show();
    }



    [RelayCommand]
    public override void Hide()
    {
        base.Hide();
    }



    [RelayCommand]
    public void ShowMainWindow()
    {
        App.Current.EnsureMainWindow();
    }


    /// <summary>
    /// 「显示主窗口」热键当前是否启用。托盘菜单里以绿/红状态灯显示。
    /// </summary>
    public bool ShowMainWindowHotkeyEnabled { get; private set => SetProperty(ref field, value); }
        = HotkeyManager.IsEnabled(HotkeyManager.ShowMainWindow.Id);


    /// <summary>
    /// 「游戏截图」热键当前是否启用。托盘菜单里以绿/红状态灯显示。
    /// </summary>
    public bool ScreenshotHotkeyEnabled { get; private set => SetProperty(ref field, value); }
        = HotkeyManager.IsEnabled(HotkeyManager.ScreenshotCapture.Id);


    /// <summary>「显示主窗口」当前按键的可读文本，显示在行尾键帽里。</summary>
    public string ShowMainWindowHotkeyText { get; private set => SetProperty(ref field, value); }
        = HotkeyManager.GetHotkeyText(HotkeyManager.ShowMainWindow.Id);


    /// <summary>「游戏截图」当前按键的可读文本，显示在行尾键帽里。</summary>
    public string ScreenshotHotkeyText { get; private set => SetProperty(ref field, value); }
        = HotkeyManager.GetHotkeyText(HotkeyManager.ScreenshotCapture.Id);


    /// <summary>
    /// 供 x:Bind 函数绑定用的 bool→Visibility 映射。
    /// 不用 <c>BoolToVisibilityConverter</c>：Window 根上的 x:Bind 取不到 StaticResource 转换器。
    /// 也不能声明成 <c>static</c> —— x:Bind 生成的代码用实例引用调用它（CS0176）。
    /// </summary>
    private Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;


    /// <inheritdoc cref="ToVisibility"/>
    private Visibility ToVisibilityReversed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;


    /// <summary>关闭状态下把键帽淡化，与状态灯一起表达「这个键当前不生效」。</summary>
    private double ToOpacity(bool enabled) => enabled ? 1 : 0.4;


    /// <summary>
    /// 从 <see cref="HotkeyManager"/> 重新读取两个热键的启用状态与按键文本。
    /// 每次弹出菜单时调用：用户可能刚在设置页改过键，托盘收不到通知，靠这里兜底刷新。
    /// </summary>
    private void RefreshHotkeyStates()
    {
        ShowMainWindowHotkeyEnabled = HotkeyManager.IsEnabled(HotkeyManager.ShowMainWindow.Id);
        ScreenshotHotkeyEnabled = HotkeyManager.IsEnabled(HotkeyManager.ScreenshotCapture.Id);
        ShowMainWindowHotkeyText = HotkeyManager.GetHotkeyText(HotkeyManager.ShowMainWindow.Id);
        ScreenshotHotkeyText = HotkeyManager.GetHotkeyText(HotkeyManager.ScreenshotCapture.Id);
    }


    /// <summary>
    /// 切换「显示主窗口」热键：立即注册/注销并持久化，无需重启。
    /// </summary>
    [RelayCommand]
    private void ToggleShowMainWindowHotkey()
    {
        int id = HotkeyManager.ShowMainWindow.Id;
        HotkeyManager.SetEnabled(id, !HotkeyManager.IsEnabled(id));
        RefreshHotkeyStates();
    }


    /// <summary>
    /// 切换「游戏截图」热键：立即注册/注销并持久化，无需重启。
    /// </summary>
    [RelayCommand]
    private void ToggleScreenshotHotkey()
    {
        int id = HotkeyManager.ScreenshotCapture.Id;
        HotkeyManager.SetEnabled(id, !HotkeyManager.IsEnabled(id));
        RefreshHotkeyStates();
    }


    [RelayCommand]
    private void Exit()
    {
        App.Current.Exit();
    }


    private void WindowEx_Closed(object sender, WindowEventArgs args)
    {
        ResidentInstanceMessenger.StopListening();
        trayIcon?.Dispose();
    }



    /// <summary>
    /// 全局热键分发。热键注册在本窗口上（见 <see cref="HotkeyManager.OwnerHandle"/>），
    /// 故 <c>WM_HOTKEY</c> 也投递到这里，而不是主窗口 —— 主窗口可能压根没创建。
    /// </summary>
    protected override unsafe IntPtr WindowSubclassProc(HWND hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == (uint)User32.WindowMessage.WM_HOTKEY)
        {
            if (wParam == 44444)
            {
                // 全局热键：打开游戏内覆盖层，失败则显示主窗口
                if (!RunningGameService.OpenOverlayWindow())
                {
                    App.Current.EnsureMainWindow();
                }
            }
            else if (wParam == 44445)
            {
                // 截图
                ScreenCaptureService.Capture();
            }
        }
        else if (uMsg == (uint)User32.WindowMessage.WM_POWERBROADCAST)
        {
            // 广播给所有顶层窗口，无需 RegisterPowerSettingNotification。不吞消息。
            var power = (User32.PowerBroadcastType)(int)wParam;
            if (power is User32.PowerBroadcastType.PBT_APMRESUMESUSPEND
                or User32.PowerBroadcastType.PBT_APMRESUMEAUTOMATIC
                or User32.PowerBroadcastType.PBT_APMRESUMECRITICAL)
            {
                AppConfig.GetService<AutoSignInService>().NotifySystemResumed();
            }
        }
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }


}
