using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Helpers;
using Starward.Language;
using System;

namespace Starward.Features.Background;

/// <summary>
/// 好感 / 满影画壁纸对话框；所选媒体写入自定义背景。
/// </summary>
[INotifyPropertyChanged]
public sealed partial class FavorWallpaperDialog : ContentDialog
{

    private readonly ILogger<FavorWallpaperDialog> _logger = AppConfig.GetLogger<FavorWallpaperDialog>();

    private readonly FavorWallpaperService _service = AppConfig.GetService<FavorWallpaperService>();


    public FavorWallpaperDialog()
    {
        this.InitializeComponent();
        this.Loaded += FavorWallpaperDialog_Loaded;
        this.Unloaded += FavorWallpaperDialog_Unloaded;
    }


    public GameId? CurrentGameId { get; set; }


    public GameBiz CurrentGameBiz { get; set; }


    [ObservableProperty]
    private string dialogTitle = Lang.FavorWallpaper_Title;


    [ObservableProperty]
    private bool isMindscapeMode;


    /// <summary>切换按钮说明：展示将进入的另一模式标题。</summary>
    public string SwitchTooltip => IsMindscapeMode ? Lang.FavorWallpaper_Title : Lang.FavorWallpaper_MindscapeTitle;


    private void FavorWallpaperDialog_Loaded(object sender, RoutedEventArgs e)
    {
        CurrentGameBiz = CurrentGameId?.GameBiz ?? GameBiz.None;
        WeakReferenceMessenger.Default.Register<AccentColorChangedMessage>(this, OnAccentColorChanged);
        FavorPanel.CurrentGameId = CurrentGameId;
        FavorPanel.CurrentGameBiz = CurrentGameBiz;
        bool mindscape = AppConfig.GetFavorWallpaperMindscapeMode(CurrentGameBiz);
        if (mindscape)
        {
            IsMindscapeMode = true;
        }
        else
        {
            _ = FavorPanel.EnsureLoadedAsync();
        }
        SyncShuffleToggle();
    }


    private void FavorWallpaperDialog_Unloaded(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }


    /// <summary>
    /// 设为背景后强调色变化时刷新对话框视觉树，使「使用中」徽标跟上。
    /// </summary>
    private void OnAccentColorChanged(object _, AccentColorChangedMessage __)
    {
        try
        {
            if (this.Content is FrameworkElement ele)
            {
                ele.RequestedTheme = ele.ActualTheme switch
                {
                    ElementTheme.Light => ElementTheme.Dark,
                    ElementTheme.Dark => ElementTheme.Light,
                    _ => ElementTheme.Default,
                };
                ele.RequestedTheme = ElementTheme.Default;
            }
        }
        catch { }
    }


    [RelayCommand]
    private void Close()
    {
        this.Hide();
    }


    /// <summary>
    /// 在好感壁纸与满影画壁纸之间切换。
    /// </summary>
    [RelayCommand]
    private void SwitchMode()
    {
        IsMindscapeMode = !IsMindscapeMode;
    }


    partial void OnIsMindscapeModeChanged(bool value)
    {
        DialogTitle = value ? Lang.FavorWallpaper_MindscapeTitle : Lang.FavorWallpaper_Title;
        OnPropertyChanged(nameof(SwitchTooltip));
        AppConfig.SetFavorWallpaperMindscapeMode(CurrentGameBiz, value);
        FavorPanel.IsMindscapeMode = value;
        _ = FavorPanel.EnsureLoadedAsync();
        SyncShuffleToggle();
    }


    /// <summary>
    /// 当前模式（好感 / 满影画）是否已开启随机模式。两者互相独立，都开启时在两类壁纸中一起随机。
    /// </summary>
    private bool IsShuffleEnabled => IsMindscapeMode
        ? AppConfig.GetMindscapeWallpaperShuffle(CurrentGameBiz)
        : AppConfig.GetFavorWallpaperShuffle(CurrentGameBiz);


    /// <summary>加载与切换模式时同步开关状态，此时不应把 Toggled 当成用户操作。</summary>
    private bool _suppressShuffleToggled;


    /// <summary>
    /// 把开关拨到当前模式对应的状态（不触发 <see cref="ToggleSwitch_Shuffle_Toggled"/>）。
    /// </summary>
    private void SyncShuffleToggle()
    {
        try
        {
            _suppressShuffleToggled = true;
            ToggleSwitch_Shuffle.IsOn = IsShuffleEnabled;
        }
        finally
        {
            _suppressShuffleToggled = false;
        }
    }


    /// <summary>
    /// 开关随机模式。只写设置，不当场换背景——随机发生在下次「重新看到背景」时：
    /// 启动软件、切换游戏、从系统托盘打开主窗口（见 <see cref="FavorWallpaperService.TryShuffleWallpaper"/>）。
    /// </summary>
    private void ToggleSwitch_Shuffle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressShuffleToggled)
        {
            return;
        }
        try
        {
            bool enable = ToggleSwitch_Shuffle.IsOn;
            if (enable && _service.GetDownloadedWallpapers(IsMindscapeMode).Count == 0)
            {
                // 候选池空开了也没用，提示先下载并把开关拨回去。
                InAppToast.MainWindow?.Warning(Lang.FavorWallpaper_ShuffleNeedDownload);
                SyncShuffleToggle();
                return;
            }
            if (IsMindscapeMode)
            {
                AppConfig.SetMindscapeWallpaperShuffle(CurrentGameBiz, enable);
            }
            else
            {
                AppConfig.SetFavorWallpaperShuffle(CurrentGameBiz, enable);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle wallpaper shuffle failed {GameBiz}", CurrentGameBiz);
            SyncShuffleToggle();
        }
    }

}
