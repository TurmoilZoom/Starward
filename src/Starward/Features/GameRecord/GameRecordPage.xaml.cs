using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord.Genshin;
using Starward.Features.GameRecord.SignIn;
using Starward.Features.GameRecord.StarRail;
using Starward.Features.GameRecord.ZZZ;
using Starward.Controls;
using Starward.Features.ViewHost;
using Starward.Frameworks;
using Starward.Helpers;
using Starward.Language;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


namespace Starward.Features.GameRecord;

public sealed partial class GameRecordPage : PageBase
{
    /// <summary>
    /// 米游社/ HoYoLAB 工具箱主页面（GameRecordPage），负责角色管理、左侧功能导航（战绩/月报等）以及子页面的容器。
    /// </summary>

    private readonly ILogger<GameRecordPage> _logger = AppConfig.GetLogger<GameRecordPage>();

    private readonly GameRecordService _gameRecordService = AppConfig.GetService<GameRecordService>();

    private readonly AutoSignInService _autoSignInService = AppConfig.GetService<AutoSignInService>();

    /// <summary>
    /// 提供与设置页相同的流体导航悬停/按压动画效果（高亮条弹簧跟随、文字偏移、物理按压反馈）。
    /// </summary>
    private readonly FluidNavigationViewHoverEffect _navHoverEffect = new();



    public GameRecordPage()
    {
        this.InitializeComponent();
    }




    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 将 B 服（bilibili）映射为国服，便于统一使用 HyperionClient 及国服逻辑。
        CurrentGameBiz = CurrentGameBiz.Value switch
        {
            GameBiz.hk4e_bilibili => GameBiz.hk4e_cn,
            GameBiz.hkrpg_bilibili => GameBiz.hkrpg_cn,
            GameBiz.nap_bilibili => GameBiz.nap_cn,
            _ => CurrentGameBiz,
        };

        // 根据区服选择客户端：国内用 HyperionClient，国际用 HoyolabClient。
        _gameRecordService.IsHoyolab = CurrentGameBiz.IsGlobalServer();

        _gameRecordService.Language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        InitializeNavigationViewItemVisibility();

        // 国服：短信验证码登录；国际服无 passport 短信，提供 WebView 网页登录
        var captchaVisible = _gameRecordService.IsHoyolab ? Visibility.Collapsed : Visibility.Visible;
        var webLoginVisible = _gameRecordService.IsHoyolab ? Visibility.Visible : Visibility.Collapsed;
        if (MenuFlyoutItem_CaptchaLogin_1 is not null)
        {
            MenuFlyoutItem_CaptchaLogin_1.Visibility = captchaVisible;
        }
        if (MenuFlyoutItem_CaptchaLogin_2 is not null)
        {
            MenuFlyoutItem_CaptchaLogin_2.Visibility = captchaVisible;
        }
        if (MenuFlyoutItem_WebLogin_1 is not null)
        {
            MenuFlyoutItem_WebLogin_1.Visibility = webLoginVisible;
        }
        if (MenuFlyoutItem_WebLogin_2 is not null)
        {
            MenuFlyoutItem_WebLogin_2.Visibility = webLoginVisible;
        }
    }




    protected override async void OnLoaded()
    {
        // 附加与「设置」页一致的流体导航动画效果（必须在 Loaded 后，视觉树就绪）。
        _navHoverEffect.Attach(NavigationView_Toolbox, NavIndicatorHost, _logger);

        // 恢复上次工具箱左侧面板（角色列表+功能菜单）的展开状态。
        if (AppConfig.HoyolabToolboxPaneOpen)
        {
            OpenNavigationViewPane();
        }
        else
        {
            CloseNavigationViewPane();
        }

        // 注册跨组件消息：角色变更时刷新列表，验证账号时弹出战绩窗口。
        WeakReferenceMessenger.Default.Register<GameRecordRoleChangedMessage>(this, (r, m) =>
        {
            LoadGameRoles(m.GameRole);
        });
        WeakReferenceMessenger.Default.Register<GameRecordVerifyAccountMessage>(this, (r, m) =>
        {
            // 优先当前页选中角色，避免多账号时打开错误战绩页
            GameRecordAccountRecovery.RequestVerifyAccount(CurrentGameBiz, CurrentRole);
        });
        WeakReferenceMessenger.Default.Register<GameRecordOpenLoginMessage>(this, (r, m) =>
        {
            // 仅打开登录，不消费 PendingOpenLogin（该标志专供跨页导航后 OnLoaded 使用，避免旧实例抢消费）
            OpenLoginForRecovery();
        });
        // 须在注册消息之后再标记存活，保证「已在工具箱」路径发出的 OpenLogin 消息能被收到
        GameRecordAccountRecovery.SetGameRecordPageAlive(true);

        await Task.Delay(16);
        NavigateTo(typeof(BlankPage));

        // 先进行免责声明检查（仅首次），通过后才加载角色、更新设备指纹并导航到默认月报页。
        if (await CheckAgreementAsync())
        {
            LoadGameRoles();
            await UpdateDeviceInfoAsync();
            await RefreshGameRoleHeadIconSilentlyAsync();
            NavigateToDefaultPage();
        }

        // 从抽卡页等非战绩页触发「重新登录」：新页 Loaded 后消费挂起标志并打开登录
        if (GameRecordAccountRecovery.ConsumePendingOpenLogin())
        {
            OpenLoginForRecovery();
        }
    }



    protected override void OnUnloaded()
    {
        // 先取消存活标记，避免卸载过程中 RequestOpenLogin 误判「已在工具箱」而只发消息
        GameRecordAccountRecovery.SetGameRecordPageAlive(false);
        WeakReferenceMessenger.Default.UnregisterAll(this);
        NavigationViewItem_BattleChronicle.Tapped -= NavigationViewItem_BattleChronicle_Tapped;
        _navHoverEffect.Detach();
        CurrentRole = null;
        GameRoleList = null!;
        _battleChronicleWindow = null;
    }




    /// <summary>
    /// 检查是否已接受米游社工具箱免责声明。
    /// 首次使用时弹出对话框（Accept 按钮带 5 秒倒计时），拒绝则跳转回启动器页面。
    /// </summary>
    /// <returns>是否允许继续加载工具箱内容。</returns>
    private async Task<bool> CheckAgreementAsync()
    {
        try
        {
            if (!AppConfig.AcceptHoyolabToolboxAgreement)
            {
                var dialog = new ContentDialog
                {
                    Title = Lang.Common_Disclaimer,
                    Content = Lang.HoyolabToolboxPage_DisclaimerContent,
                    PrimaryButtonText = Lang.Common_Accept + " (5s)",
                    SecondaryButtonText = Lang.Common_Reject,
                    IsPrimaryButtonEnabled = false,
                    DefaultButton = ContentDialogButton.Secondary,
                    XamlRoot = this.XamlRoot,
                };
                var resultTask = dialog.ShowAsync();
                bool cancel = false;

                // 实现 5 秒倒计时：每 0.1s 检查一次对话框是否被关闭，防止用户提前操作。
                for (int i = 0; i < 5; i++)
                {
                    for (int j = 0; j < 10; j++)
                    {
                        await Task.Delay(100);
                        if (resultTask.Status is Windows.Foundation.AsyncStatus.Completed)
                        {
                            cancel = true;
                            break;
                        }
                    }
                    if (cancel)
                    {
                        break;
                    }
                    dialog.PrimaryButtonText = Lang.Common_Accept + $" ({4 - i}s)";
                }

                dialog.PrimaryButtonText = Lang.Common_Accept;
                dialog.IsPrimaryButtonEnabled = true;
                var result = await resultTask;

                if (result is ContentDialogResult.Primary)
                {
                    AppConfig.AcceptHoyolabToolboxAgreement = true;
                }
                else
                {
                    // 拒绝或关闭 → 返回启动器页面，不进入工具箱。
                    WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(GameLauncherPage)));
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check agreement.");
            return false;
        }
    }




    #region Navigation Style


    /// <summary>
    /// 控制左侧工具箱面板内容区域的边距（展开时收紧，收起时留空）。
    /// </summary>
    public Thickness NavigationViewItemContentMargin { get; set => SetProperty(ref field, value); } = new Thickness(-2, 0, 0, 0);


    /// <summary>
    /// 点击宽头像区域 → 收起左侧面板（节省空间）。
    /// </summary>
    private void Grid_Avatar_1_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        CloseNavigationViewPane();
    }


    /// <summary>
    /// 点击窄头像 → 展开左侧面板（显示角色列表和功能菜单）。
    /// </summary>
    private void Border_Avatar_2_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        OpenNavigationViewPane();
    }


    /// <summary>
    /// 展开工具箱左侧面板，并持久化状态。
    /// </summary>
    private void OpenNavigationViewPane()
    {
        NavigationViewItemContentMargin = new Thickness(-2, 0, 0, 0);
        NavigationView_Toolbox.IsPaneOpen = true;
        Grid_Avatar_1.Visibility = Visibility.Visible;
        Border_Avatar_2.Visibility = Visibility.Collapsed;
        AppConfig.HoyolabToolboxPaneOpen = true;
    }


    /// <summary>
    /// 收起工具箱左侧面板，并持久化状态到设置。
    /// </summary>
    private void CloseNavigationViewPane()
    {
        NavigationViewItemContentMargin = new Thickness(2, 0, 0, 0);
        NavigationView_Toolbox.IsPaneOpen = false;
        Grid_Avatar_1.Visibility = Visibility.Collapsed;
        Border_Avatar_2.Visibility = Visibility.Visible;
        AppConfig.HoyolabToolboxPaneOpen = false;
    }


    /// <summary>
    /// 根据当前游戏显示对应的左侧工具箱菜单项（战绩 + 各游戏专属月报/札记等），并设置对应战绩图片。
    /// </summary>
    private void InitializeNavigationViewItemVisibility()
    {
        if (CurrentGameBiz.Game is GameBiz.bh3)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            // 崩坏3战绩图片（背景图）
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/4d94fbd5ff63c8b4344876ce21e04d10_2581928258151711511.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.hk4e)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_TravelersDiary.Visibility = Visibility.Visible;
            NavigationViewItem_SpiralAbyss.Visibility = Visibility.Visible;
            NavigationViewItem_ImaginariumTheater.Visibility = Visibility.Visible;
            NavigationViewItem_StygianOnslaught.Visibility = Visibility.Visible;
            // 原神战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/ced4deac2162690105bbc8baad2b51a3_4109616186965788891.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.hkrpg)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_TrailblazeMonthlyCalendar.Visibility = Visibility.Visible;
            NavigationViewItem_SimulatedUniverse.Visibility = Visibility.Visible;
            NavigationViewItem_ForgottenHall.Visibility = Visibility.Visible;
            NavigationViewItem_PureFiction.Visibility = Visibility.Visible;
            NavigationViewItem_ApocalypticShadow.Visibility = Visibility.Visible;
            NavigationViewItem_ChallengePeak.Visibility = Visibility.Visible;
            // 星穹铁道战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/ade9545750299456a3fcbc8c3b63521d_2941971308029698042.png"));
        }
        else if (CurrentGameBiz.Game is GameBiz.nap)
        {
            NavigationViewItem_BattleChronicle.Visibility = Visibility.Visible;
            NavigationViewItem_InterKnotMonthlyReport.Visibility = Visibility.Visible;
            NavigationViewItem_ShiyuDefense.Visibility = Visibility.Visible;
            NavigationViewItem_DeadlyAssault.Visibility = Visibility.Visible;
            // 绝区零战绩图片
            Image_BattleChronicle.Source = new BitmapImage(new("ms-appx:///Assets/Image/bc8f0b7384b306c80f2a1fcca9f3d14b_8590605504999484795.png"));
        }
    }




    #endregion




    #region Game Role Info



    /// <summary>
    /// 当前选中的游戏角色（含 Cookie），用于后续所有米游社 API 请求。
    /// </summary>
    public GameRecordRole? CurrentRole
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AvatarUrl));
            }
        }
    }


    /// <summary>
    /// 当前游戏下所有已添加的角色列表（用于角色切换下拉）。
    /// </summary>
    public List<GameRecordRole> GameRoleList { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 头像地址：优先使用角色 HeadIcon，否则根据区服显示 Hyperion / HoYoLAB 默认图标。
    /// </summary>
    public string AvatarUrl => !string.IsNullOrWhiteSpace(CurrentRole?.HeadIcon) ? CurrentRole.HeadIcon : $"ms-appx:///Assets/Image/icon_{(CurrentGameBiz.IsGlobalServer() ? "hoyolab" : "hyperion")}.png";



    /// <summary>
    /// 加载当前游戏的角色列表。
    /// 优先使用传入角色或上次选择的角色，否则取第一个。
    /// </summary>
    private void LoadGameRoles(GameRecordRole? role = null)
    {
        try
        {
            if (role != null)
            {
                _gameRecordService.SetLastSelectGameRecordRole(CurrentGameBiz, role);
            }
            role ??= _gameRecordService.GetLastSelectGameRecordRoleOrTheFirstOne(CurrentGameBiz);
            var list = _gameRecordService.GetGameRoles(CurrentGameBiz);
            CurrentRole = role ?? list.FirstOrDefault();
            GameRoleList = list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load game roles ({gameBiz}).", CurrentGameBiz);
        }
    }




    /// <summary>
    /// 弹出登录方式菜单，供错误恢复（重新登录）与未登录引导复用。
    /// 无角色时锚定未登录按钮；已有角色时锚定角色列表按钮。
    /// </summary>
    private void OpenLoginMenu()
    {
        try
        {
            if (CurrentRole is null)
            {
                Flyout_LoginMenu_1.ShowAt(Button_Login);
            }
            else
            {
                Flyout_LoginMenu_2.ShowAt(Button_GameRoles);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open login menu");
        }
    }


    /// <summary>
    /// 错误恢复 / 跨页重新登录入口：国际服直接网页登录，国服弹登录菜单。
    /// </summary>
    private void OpenLoginForRecovery()
    {
        // 按本页当前游戏判断，不读 GameRecordService.IsHoyolab：那个字段会被后台任务改到
        if (CurrentGameBiz.IsGlobalServer())
        {
            WebLogin();
        }
        else
        {
            OpenLoginMenu();
        }
    }


    /// <summary>
    /// 国际服（HoYoLAB）网页登录：嵌入 WebView2 打开官网，登录后读取 Cookie 入库。
    /// 国服优先短信验证码，不提供此项。
    /// </summary>
    [RelayCommand]
    private void WebLogin()
    {
        NavigateTo(typeof(LoginPage), CurrentGameBiz);
    }




    [RelayCommand]
    private async Task RefreshGameRoleInfoAsync()
    {
        try
        {
            if (CurrentRole is null)
            {
                await _gameRecordService.RefreshAllGameRolesInfoAsync(CurrentGameBiz.IsGlobalServer());
            }
            else
            {
                await _gameRecordService.RefreshGameRoleInfoAsync(CurrentRole);
            }
            LoadGameRoles();
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            HandleMiHoYoApiException(ex, CurrentGameBiz, CurrentRole);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            HandleMiHoYoHttpException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh game role info ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
            InAppToast.MainWindow?.Error(ex);
        }
    }



    private void ListView_GameRoles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is GameRecordRole role)
        {
            CurrentRole = role;
            _gameRecordService.SetLastSelectGameRecordRole(CurrentGameBiz, role);
            // 网页登录过程中切换角色列表选中项时不要把 LoginPage 冲掉
            if (frame.SourcePageType?.Name is not nameof(LoginPage) && frame.SourcePageType is not null)
            {
                NavigateTo(frame.SourcePageType, force_navigate: true);
            }
        }
    }




    private void MenuFlyoutItem_CopyCookie_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: GameRecordRole role })
            {

                ClipboardHelper.SetText(role.Cookie);
            }
        }
        catch { }
    }



    private void MenuFlyoutItem_DeleteGameRole_Click(object sender, RoutedEventArgs e)
    {
        GameRecordRole? gameRole = null;
        try
        {
            if (sender is FrameworkElement { Tag: GameRecordRole role })
            {
                gameRole = role;
                _gameRecordService.DeleteGameRole(role);
                LoadGameRoles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete game role ({gameBiz}, {uid}).", gameRole?.GameBiz, gameRole?.Uid);
        }
    }



    [RelayCommand]
    private async Task InputCookieAsync()
    {
        try
        {
            var textbox = new TextBox
            {
                IsSpellCheckEnabled = false,
            };
            var dialog = new ContentDialog
            {
                Title = Lang.HoyolabToolboxPage_InputCookie,
                Content = textbox,
                PrimaryButtonText = Lang.Common_Confirm,
                SecondaryButtonText = Lang.Common_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result is ContentDialogResult.Primary)
            {
                var cookie = textbox.Text;
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    _logger.LogInformation("Input cookie is null or white space.");
                    return;
                }
                bool isHoyolab = CurrentGameBiz.IsGlobalServer();
                var user = await _gameRecordService.AddRecordUserAsync(cookie, isHoyolab);
                var roles = await _gameRecordService.AddGameRolesAsync(cookie, isHoyolab);
                _autoSignInService.NotifyRolesReauthenticated(roles);
                InAppToast.MainWindow?.Success(null, string.Format(Lang.LoginPage_AlreadyAddedGameRoles, roles.Count, string.Join("\r\n", roles.Select(x => $"{x.Nickname}  {x.Uid}"))), 5000);
                LoadGameRoles(roles.FirstOrDefault(x => x.GameBiz == CurrentGameBiz.ToString()));
            }
        }
        catch (miHoYoApiException ex)
        {
            _logger.LogError(ex, "Input cookie");
            HandleMiHoYoApiException(ex, CurrentGameBiz, CurrentRole);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Input cookie");
            HandleMiHoYoHttpException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input cookie");
            InAppToast.MainWindow?.Error(ex);
        }
    }



    /// <summary>
    /// 国服短信验证码登录：发码（可含独立极验弹层）→ 输入验证码 → 换票入库。
    /// </summary>
    [RelayCommand]
    private async Task CaptchaLoginAsync()
    {
        // 同上：按本页当前游戏判断。短信验证码登录仅国服
        if (CurrentGameBiz.IsGlobalServer())
        {
            return;
        }

        try
        {
            var dialog = new CaptchaLoginDialog();
            var result = await dialog.ShowAsync(XamlRoot);
            if (result is not ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.CookieResult))
            {
                return;
            }

            string cookie = dialog.CookieResult;
            var user = await _gameRecordService.AddRecordUserAsync(cookie, isHoyolab: false);
            var roles = await _gameRecordService.AddGameRolesAsync(cookie, isHoyolab: false);
            _autoSignInService.NotifyRolesReauthenticated(roles);
            InAppToast.MainWindow?.Success(null, string.Format(Lang.LoginPage_AlreadyAddedGameRoles, roles.Count, string.Join("\r\n", roles.Select(x => $"{x.Nickname}  {x.Uid}"))), 5000);
            LoadGameRoles(roles.FirstOrDefault(x => x.GameBiz == CurrentGameBiz.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Captcha login");
            // 入库阶段异常：验证码登录上下文统一走 Factory（retcode 语义与战绩分离）
            MiHoYoApiErrorFeedbackFactory.Show(ex, MiHoYoApiContext.PassportCaptcha);
        }
    }



    /// <summary>
    /// 静默更新当前角色的头像（调用 index 接口获取最新 head icon）。
    /// 有内存 5 分钟缓存去重。
    /// </summary>
    private async Task RefreshGameRoleHeadIconSilentlyAsync()
    {
        try
        {
            if (CurrentRole is not null)
            {
                await _gameRecordService.UpdateGameRoleHeadIconAsync(CurrentRole);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update game role head icon silently ({gameBiz}, {uid}).", CurrentRole?.GameBiz, CurrentRole?.Uid);
        }
    }




    #endregion




    #region Navigate



    /// <summary>
    /// 导航到子页面（旅行者札记、开拓月历、绳网月报、深渊等）。
    /// 默认参数为当前角色。
    /// </summary>
    private void NavigateTo(Type? page, object? parameter = null, bool force_navigate = false)
    {
        if (page is null)
        {
            return;
        }
        if (!force_navigate && frame.SourcePageType == page)
        {
            return;
        }
        frame.Navigate(page, parameter ?? CurrentRole);
    }



    /// <summary>
    /// 右侧 Frame 每次导航到子页后，对其内容根面板的直接子元素播放「从右滑入 + 淡入」错峰级联入场。
    /// 已 Loaded 则立即播放；否则挂一次性 Loaded 回调，与设置页 <c>Frame_Setting_Navigated</c> 对齐。
    /// </summary>
    /// <param name="sender">触发导航的 Frame。</param>
    /// <param name="e">导航事件参数；从 <see cref="NavigationEventArgs.Content"/> 取新页面。</param>
    private void frame_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is Page page)
        {
            if (page.IsLoaded)
            {
                EntranceAnimation.PlayFromRight(page);
            }
            else
            {
                void OnPageLoaded(object s, RoutedEventArgs args)
                {
                    page.Loaded -= OnPageLoaded;
                    EntranceAnimation.PlayFromRight(page);
                }
                page.Loaded += OnPageLoaded;
            }
        }
    }



    /// <summary>
    /// 根据当前游戏导航到默认统计页面：绝区零→绳网月报，原神→旅行者札记，铁道→开拓月历。
    /// 并同步选中左侧工具箱菜单项。
    /// </summary>
    private void NavigateToDefaultPage()
    {
        Type? type = CurrentGameBiz.Game switch
        {
            GameBiz.nap => typeof(InterKnotMonthlyReportPage),
            GameBiz.hk4e => typeof(TravelersDiaryPage),
            GameBiz.hkrpg => typeof(TrailblazeCalendarPage),
            _ => null,
        };
        if (type is null)
        {
            return;
        }
        NavigateTo(type);

        // 同步更新左侧导航栏选中状态，使菜单高亮与内容一致。
        NavigationViewItem? navItem = CurrentGameBiz.Game switch
        {
            GameBiz.nap => NavigationViewItem_InterKnotMonthlyReport,
            GameBiz.hk4e => NavigationViewItem_TravelersDiary,
            GameBiz.hkrpg => NavigationViewItem_TrailblazeMonthlyCalendar,
            _ => null,
        };
        if (navItem is not null)
        {
            NavigationView_Toolbox.SelectedItem = navItem;
        }
    }



    /// <summary>
    /// 左侧工具箱菜单点击时，根据 Tag 导航到对应页面（月报、深渊等）。
    /// </summary>
    private void NavigationView_Toolbox_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            var item = args.InvokedItemContainer as NavigationViewItem;
            if (item != null)
            {
                if (args.InvokedItemContainer?.IsSelected ?? false)
                {
                    return;
                }
                // Tag 与页面类型名对应，实现菜单到页面的映射。
                var type = item.Tag switch
                {
                    nameof(TravelersDiaryPage) => typeof(TravelersDiaryPage),
                    nameof(SpiralAbyssPage) => typeof(SpiralAbyssPage),
                    nameof(ImaginariumTheaterPage) => typeof(ImaginariumTheaterPage),
                    nameof(StygianOnslaughtPage) => typeof(StygianOnslaughtPage),
                    nameof(TrailblazeCalendarPage) => typeof(TrailblazeCalendarPage),
                    nameof(SimulatedUniversePage) => typeof(SimulatedUniversePage),
                    nameof(ForgottenHallPage) => typeof(ForgottenHallPage),
                    nameof(PureFictionPage) => typeof(PureFictionPage),
                    nameof(ApocalypticShadowPage) => typeof(ApocalypticShadowPage),
                    nameof(ChallengePeakPage) => typeof(ChallengePeakPage),
                    nameof(InterKnotMonthlyReportPage) => typeof(InterKnotMonthlyReportPage),
                    nameof(ShiyuDefensePage) => typeof(ShiyuDefensePage),
                    nameof(DeadlyAssaultPage) => typeof(DeadlyAssaultPage),
                    _ => null,
                };
                NavigateTo(type);
            }
        }
        catch { }
    }



    private void NavigationViewItem_BattleChronicle_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ShowBattleChronicleWindow();
    }



    private BattleChronicleWindow? _battleChronicleWindow;



    /// <summary>
    /// 显示战绩窗口（深渊/忘却/虚构等详细战斗数据）。
    /// 支持特定错误码（如 1034）时由外部触发。
    /// </summary>
    private void ShowBattleChronicleWindow()
    {
        // 窗口关闭后 AppWindow is null，需要重新创建实例
        if (_battleChronicleWindow?.AppWindow is null)
        {
            _battleChronicleWindow = new BattleChronicleWindow
            {
                CurrentRole = CurrentRole,
            };
        }
        else if (_battleChronicleWindow.CurrentRole != CurrentRole)
        {
            _battleChronicleWindow.CurrentRole = CurrentRole;
        }
        _battleChronicleWindow.Activate();
    }




    #endregion




    #region Device Info




    /// <summary>
    /// 在工具箱加载时同步设备指纹（仅国内服）。首次或超过 3 天会调用 public-data-api 获取新 fp。
    /// 用于后续所有 Hyperion 请求的 x-rpc-device-fp 头，降低风控概率。
    /// </summary>
    /// <returns>表示初始化同步已完成或已记录异常的任务。</returns>
    private async Task UpdateDeviceInfoAsync()
    {
        try
        {
            await _gameRecordService.UpdateDeviceFpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update device info");
        }
    }





    #endregion





    /// <summary>
    /// 获取战绩页面应展示的米哈游 API 错误文案。
    /// </summary>
    /// <param name="ex">包含接口原始消息和返回码的米哈游 API 异常。</param>
    /// <returns>按战绩接口语义分类后的本地化错误文案。</returns>
    public static string GetMiHoYoApiExceptionMessage(miHoYoApiException ex)
    {
        return MiHoYoApiErrorFeedbackFactory.Create(ex, MiHoYoApiContext.GameRecord).Message;
    }


    /// <summary>
    /// 统一处理战绩相关的 <see cref="miHoYoApiException"/>，并提供站内登录或验证恢复入口。
    /// 恢复动作不依赖本页是否已加载（抽卡页、启动器等也可点按钮生效）。
    /// </summary>
    /// <param name="ex">米哈游 API 异常。</param>
    /// <param name="preferredBiz">验证账号时优先使用的游戏区服；为 null 时可从 preferredRole 推断。</param>
    /// <param name="preferredRole">触发错误时的角色；校验时应优先打开该角色的官方战绩页。</param>
    public static void HandleMiHoYoApiException(miHoYoApiException ex, GameBiz? preferredBiz = null, GameRecordRole? preferredRole = null)
    {
        var feedback = MiHoYoApiErrorFeedbackFactory.Create(ex, MiHoYoApiContext.GameRecord);
        MiHoYoApiErrorFeedbackFactory.Show(feedback, action =>
        {
            if (action is MiHoYoApiRecoveryAction.Relogin)
            {
                GameRecordAccountRecovery.RequestOpenLogin();
            }
            else if (action is MiHoYoApiRecoveryAction.VerifyAccount)
            {
                GameRecordAccountRecovery.RequestVerifyAccount(preferredBiz, preferredRole);
            }
        });
    }



    /// <summary>
    /// 统一处理战绩相关的 HTTP 请求异常，并按状态码显示可恢复的本地化反馈。
    /// </summary>
    /// <param name="ex">HTTP 请求异常。</param>
    public static void HandleMiHoYoHttpException(HttpRequestException ex)
    {
        var feedback = MiHoYoApiErrorFeedbackFactory.Create(ex, MiHoYoApiContext.GameRecord);
        MiHoYoApiErrorFeedbackFactory.Show(feedback, action =>
        {
            if (action is MiHoYoApiRecoveryAction.Relogin)
            {
                GameRecordAccountRecovery.RequestOpenLogin();
            }
        });
    }


}
