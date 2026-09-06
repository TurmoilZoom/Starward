using Dapper;
using Starward.Core;
using Starward.Features.Database;
using Starward.Features.GameLauncher;
using Starward.Features.ViewHost;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Starward;

public static partial class AppConfig
{

    // 注意：本文件包含大量用户设置属性。
    // 静态设置直接暴露为属性；按游戏区分的动态设置通过 GetXXX(biz) / SetXXX(biz, value) 访问，
    // 底层统一走 GetValue<T> / SetValue<T> + 数据库 Setting 表持久化。

    #region Static Setting（全局静态设置属性）


    public static bool EnablePreviewRelease
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 是否在启动时自动检查并推送新版本可用弹窗；手动「检查更新」不受此开关影响。
    /// </summary>
    public static bool EnableUpdateNotification
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 是否静默更新：后台下载新版本，退出应用后由 Velopack 静默安装。
    /// 仅在 <see cref="EnableUpdateNotification"/> 开启时生效；手动「检查更新」不受此开关影响。
    /// </summary>
    public static bool EnableSilentUpdate
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 上次已展示过更新说明（或已对齐）的应用版本。静默更新后与 <see cref="AppVersion"/> 比较，用于弹出更新内容。
    /// </summary>
    public static string? LastAppVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 静默更新已下载、待下次启动展示发行说明。手动更新路径不会置位。
    /// </summary>
    public static bool PendingSilentUpdateContent
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static string? IgnoreVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static bool EnableBannerAndPost
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 是否在首页显示游戏时长（PlayTimeButton）。默认开启，与原先始终显示的行为一致。
    /// </summary>
    public static bool EnablePlayTime
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool IgnoreRunningGame
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ShowNoviceGacha
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ShowChronicledWish
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 上次完成抽卡物品名称回写所用的语言（规整后的语言键，如 "zh-cn"）。
    /// 用于判断软件语言是否变化：与当前 UI 语言不一致（含首次启动后为 null）时触发存量记录名称迁移。
    /// 取代旧的 GachaLanguage（抽卡名称现跟随软件 UI 语言）。
    /// </summary>
    public static string? LastGachaNameLanguage
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? AccentColor
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    [Obsolete("已不用", true)]
    public static bool UseOneBg
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool AcceptHoyolabToolboxAgreement
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool HoyolabToolboxPaneOpen
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool EnableSystemTrayIcon
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ExitWhenClosing
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 主窗口关闭选项，隐藏/退出
    /// </summary>
    public static MainWindowCloseOption CloseWindowOption
    {
        get => GetValue<MainWindowCloseOption>();
        set => SetValue(value);
    }

    public static bool UseSystemThemeColor
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool EnableNavigationViewLeftCompact
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ToolbarPinned
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 首页功能工具栏布局（位置 / 贴边）。格式见 GameLauncherPage 中 Right Toolbar 区域的读写逻辑。
    /// </summary>
    public static string? GameLauncherRightToolbarLayout
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 是否已看过（或用过）首页右侧功能栏拖拽引导。默认 false。
    /// 不能用 <see cref="GameLauncherRightToolbarLayout"/> 是否有值代替：进过首页就会写出 free|x|y。
    /// </summary>
    public static bool HasSeenRightToolbarDragHint
    {
        get => GetValue(false);
        set => SetValue(value);
    }

    public static StartGameAction StartGameAction
    {
        get => GetValue(Starward.Features.GameLauncher.StartGameAction.Minimize);
        set => SetValue(value);
    }

    public static string? HyperionDeviceId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? HyperionDeviceFp
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static DateTimeOffset HyperionDeviceFpLastUpdateTime
    {
        get => GetValue<DateTimeOffset>();
        set => SetValue(value);
    }

    /// <summary>
    /// 上次因国服 GameRecord 请求失败而尝试刷新设备指纹的时间。
    /// 即使刷新请求失败也会写入，用于跨应用重启限制重复请求。
    /// </summary>
    public static DateTimeOffset HyperionDeviceFpLastFailureUpdateAttemptTime
    {
        get => GetValue<DateTimeOffset>();
        set => SetValue(value);
    }

    /// <summary>getFp 的 seed_id，与指纹一起复用。</summary>
    public static string? HyperionDeviceFpSeedId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>getFp 的 seed_time。</summary>
    public static string? HyperionDeviceFpSeedTime
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>getFp 体中的 16 位 hex device_id（模拟 ANDROID_ID）。</summary>
    public static string? HyperionDeviceAndroidId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 设备指纹 ext_fields 载荷版本。低于当前实现版本时强制重新 getFp（旧载荷含 windows 硬件信息，易 10041）。
    /// </summary>
    public static int HyperionDeviceFpPayloadVersion
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    /// <summary>
    /// 当前选择的游戏区服
    /// </summary>
    public static GameBiz CurrentGameBiz
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? SelectedGameBizs
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 固定待选择的游戏区服图标
    /// </summary>
    public static bool IsGameBizSelectorPinned
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static string? DefaultGameInstallationPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static int SpeedLimitKBPerSecond
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 缓存的游戏信息 <see cref="Starward.Core.HoYoPlay.GameInfo"/>
    /// </summary>
    public static string? CachedGameInfo
    {
        get => DatabaseService.GetValue<string>(nameof(CachedGameInfo), out _, default);
        set => DatabaseService.SetValue(nameof(CachedGameInfo), value);
    }

    /// <summary>
    /// 更新完成后自动重启
    /// </summary>
    public static bool AutoRestartWhenUpdateFinished
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 保持 RPC 服务在后台运行
    /// </summary>
    public static bool KeepRpcServerRunningInBackground
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 安装游戏时自动创建子文件夹
    /// </summary>
    public static bool AutomaticallyCreateSubfolderForInstall
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 崩坏3国际服多区服选项
    /// </summary>
    public static string? LastGameIdOfBH3Global
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 启用硬链接
    /// </summary>
    public static bool EnableHardLink
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 原神HDR
    /// </summary>
    public static bool EnableGenshinHDR
    {
        get => GetValue(false);
        set => SetValue(value);
    }

    /// <summary>
    /// 截图文件夹
    /// </summary>
    public static string? ScreenshotFolder
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    /// <summary>
    /// 「显示主窗口」快捷键是否启用，可在系统托盘菜单里随时切换。
    /// 关闭后不向系统注册该热键，但按键配置仍保留，重新打开即按原键恢复。
    /// </summary>
    public static bool EnableShowMainWindowHotkey
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 「游戏截图」快捷键是否启用，可在系统托盘菜单里随时切换。
    /// 关闭后不向系统注册该热键，但按键配置仍保留，重新打开即按原键恢复。
    /// </summary>
    public static bool EnableScreenshotCaptureHotkey
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 显示主窗口快捷键
    /// </summary>
    public static string? ShowMainWindowHotkey
    {
        // Alt + S
        get => GetValue("1+83");
        set => SetValue(value);
    }

    /// <summary>
    /// 截图快捷键
    /// </summary>
    public static string? ScreenshotCaptureHotkey
    {
        // Alt + D
        get => GetValue("1+68");
        set => SetValue(value);
    }

    /// <summary>
    /// 手柄控制
    /// </summary>
    public static bool EnableGamepadSimulateInput
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static int GamepadGuideButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    public static string? GamepadShareButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static string? GamepadGuideButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }

    public static int GamepadShareButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }

    /// <summary>
    /// 当前是否由本软件接管了 GameBar 引导键（注册表 <c>UseNexusForGameBarEnabled=0</c> 是我们写的）。
    /// 进程被强杀/崩溃时注册表值会残留，靠这个标记在下次启动时自愈还原，同时避免覆盖用户自己关掉的引导键。
    /// </summary>
    public static bool GamepadGuideButtonTakenOver
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool AutoConvertScreenshotToSDR
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    public static bool AutoCopyScreenshotToClipboard
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 截图链路色彩管理（HDR 模式始终启用）
    /// </summary>
    public static bool EnableScreenshotColorManagement
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 0: PNG, 1: AVIF, 2: JPEG XL
    /// </summary>
    public static int ScreenCaptureSavedFormat
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 0: Middle, 1: High, 2: Lossless
    /// </summary>
    public static int ScreenCaptureEncodeQuality
    {
        get => GetValue(1);
        set => SetValue(value);
    }

    public static bool EnableGamepadController
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 使用 CMD 启动游戏 <see href="https://github.com/Scighost/Starward/issues/1634"/>
    /// </summary>
    public static bool StartGameWithCMD
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 游戏时长统计柱状图的区间模式：0 最近 15 天、1 最近 12 周、2 最近 12 月、3 自定义年月。
    /// </summary>
    public static int PlayTimeStatsBarRange
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 游戏时长统计柱状图自定义模式选中的年份，0 表示未选择（用当前年）。
    /// </summary>
    public static int PlayTimeStatsBarYear
    {
        get => GetValue(0);
        set => SetValue(value);
    }

    /// <summary>
    /// 游戏时长统计柱状图自定义模式选中的月份，0 表示全年。
    /// </summary>
    public static int PlayTimeStatsBarMonth
    {
        get => GetValue(0);
        set => SetValue(value);
    }


    #endregion



    #region Dynamic Setting（按游戏区服持久化的动态设置）

    /// <summary>
    /// 获取指定游戏的背景设置（通常是背景图片路径或标识）。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <returns>背景设置字符串。</returns>
    public static string? GetBg(GameBiz biz)
    {
        return GetValue<string>(default, $"bg_{biz}");
    }

    public static void SetBg(GameBiz biz, string? value)
    {
        SetValue(value, $"bg_{biz}");
    }


    public static bool GetUseVersionPoster(GameBiz biz)
    {
        return GetValue<bool>(default, $"use_version_poster_{biz}");
    }

    public static void SetUseVersionPoster(GameBiz biz, bool value)
    {
        SetValue(value, $"use_version_poster_{biz}");
    }


    /// <summary>
    /// 上次官方视频背景是否处于暂停（显示静态图）状态。
    /// 用于下次启动/切换游戏时，在仍有官方视频背景的情况下恢复暂停。
    /// </summary>
    public static bool GetStopOfficialVideo(GameBiz biz)
    {
        return GetValue<bool>(default, $"stop_official_video_{biz}");
    }

    /// <summary>
    /// 记录官方视频背景是否暂停。仅应在当前显示类型为官方视频时写入。
    /// </summary>
    public static void SetStopOfficialVideo(GameBiz biz, bool value)
    {
        SetValue(value, $"stop_official_video_{biz}");
    }


    public static string? GetVersionPoster(GameBiz biz)
    {
        return GetValue<string>(default, $"version_poster_{biz}");
    }

    public static void SetVersionPoster(GameBiz biz, string? value)
    {
        SetValue(value, $"version_poster_{biz}");
    }


    public static string? GetCustomBg(GameBiz biz)
    {
        return GetValue<string>(default, $"custom_bg_{biz}");
    }

    public static void SetCustomBg(GameBiz biz, string? value)
    {
        SetValue(value, $"custom_bg_{biz}");
    }


    public static bool GetEnableCustomBg(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_custom_bg_{biz}");
    }

    public static void SetEnableCustomBg(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_custom_bg_{biz}");
    }


    /// <summary>
    /// 好感壁纸对话框上次是否停在满影画模式。
    /// </summary>
    public static bool GetFavorWallpaperMindscapeMode(GameBiz biz)
    {
        return GetValue<bool>(default, $"favor_wallpaper_mindscape_{biz}");
    }


    /// <summary>
    /// 记住好感壁纸对话框的满影画 / 好感切换。
    /// </summary>
    public static void SetFavorWallpaperMindscapeMode(GameBiz biz, bool value)
    {
        SetValue(value, $"favor_wallpaper_mindscape_{biz}");
    }


    /// <summary>
    /// 好感壁纸随机播放：软件启动后首次显示该游戏背景时，从已下载的好感壁纸中随机挑一张。
    /// </summary>
    public static bool GetFavorWallpaperShuffle(GameBiz biz)
    {
        return GetValue<bool>(default, $"favor_wallpaper_shuffle_{biz}");
    }


    /// <summary>
    /// 设置好感壁纸随机播放开关。
    /// </summary>
    public static void SetFavorWallpaperShuffle(GameBiz biz, bool value)
    {
        SetValue(value, $"favor_wallpaper_shuffle_{biz}");
    }


    /// <summary>
    /// 满影画壁纸随机播放：与好感壁纸互相独立，两者都开启时在两类已下载壁纸中一起随机。
    /// </summary>
    public static bool GetMindscapeWallpaperShuffle(GameBiz biz)
    {
        return GetValue<bool>(default, $"mindscape_wallpaper_shuffle_{biz}");
    }


    /// <summary>
    /// 设置满影画壁纸随机播放开关。
    /// </summary>
    public static void SetMindscapeWallpaperShuffle(GameBiz biz, bool value)
    {
        SetValue(value, $"mindscape_wallpaper_shuffle_{biz}");
    }


    /// <summary>
    /// 每日自动签到（软件启动后静默批量签到），按游戏区分，默认关闭。
    /// </summary>
    /// <param name="biz">游戏业务线，如 hk4e_cn。</param>
    /// <returns>该游戏是否已开启自动签到。</returns>
    public static bool GetAutoSignInEnabled(GameBiz biz)
    {
        return GetValue<bool>(default, $"auto_sign_in_enabled_{biz}");
    }

    /// <summary>
    /// 设置指定游戏的自动签到开关。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">是否开启。</param>
    public static void SetAutoSignInEnabled(GameBiz biz, bool value)
    {
        SetValue(value, $"auto_sign_in_enabled_{biz}");
    }


    /// <summary>
    /// 获取指定游戏的安装路径。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <returns>安装目录完整路径或 null。</returns>
    public static string? GetGameInstallPath(GameBiz biz)
    {
        return GetValue<string>(default, $"install_path_{biz}");
    }

    /// <summary>
    /// 设置指定游戏的安装路径。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">安装目录路径。</param>
    public static void SetGameInstallPath(GameBiz biz, string? value)
    {
        SetValue(value, $"install_path_{biz}");
    }


    public static bool GetGameInstallPathRemovable(GameBiz biz)
    {
        return GetValue<bool>(default, $"install_path_removable_{biz}");
    }

    public static void SetGameInstallPathRemovable(GameBiz biz, bool value)
    {
        SetValue(value, $"install_path_removable_{biz}");
    }


    public static bool GetEnableThirdPartyTool(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_third_party_tool_{biz}");
    }

    public static void SetEnableThirdPartyTool(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_third_party_tool_{biz}");
    }


    public static string? GetThirdPartyToolPath(GameBiz biz)
    {
        return GetValue<string>(default, $"third_party_tool_path_{biz}");
    }

    public static void SetThirdPartyToolPath(GameBiz biz, string? value)
    {
        SetValue(value, $"third_party_tool_path_{biz}");
    }


    public static string? GetStartArgument(GameBiz biz)
    {
        return GetValue<string>(default, $"start_argument_{biz}");
    }

    public static void SetStartArgument(GameBiz biz, string? value)
    {
        SetValue(value, $"start_argument_{biz}");
    }


    /// <summary>
    /// 获取指定游戏的额外启动配置文件列表（不含默认配置文件，默认配置文件的数据仍存于 legacy 键）。
    /// </summary>
    public static List<GameLaunchProfile> GetExtraLaunchProfiles(GameBiz biz)
    {
        var result = new List<GameLaunchProfile>();
        string? json = GetValue<string>(default, $"launch_profiles_extra_{biz}");
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        List<GameLaunchProfile>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<GameLaunchProfile>>(json, JsonSerializerOptions);
        }
        catch
        {
            return result;
        }
        if (list is null)
        {
            return result;
        }
        // 仅接受 config2…configN：唯一、不与 config1 冲突，无数量上限。
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GameLaunchProfile.DefaultId };
        foreach (GameLaunchProfile p in list)
        {
            p.Id = GameLaunchProfile.NormalizeId(p.Id);
            if (string.IsNullOrEmpty(p.Id) || used.Contains(p.Id) || !GameLaunchProfile.IsKnownId(p.Id) || GameLaunchProfile.IsDefaultId(p.Id))
            {
                p.Id = GameLaunchProfile.GetNextAvailableId(used);
                // Id 被重分配后清空名称，由界面按 configN →「配置文件 N」重新生成
                p.Name = "";
            }
            used.Add(p.Id);
            result.Add(p);
        }
        return result;
    }


    /// <summary>
    /// 按内部名获取额外启动配置文件。null/空/none/config1/未找到 时返回 null（config1 由调用方读 legacy 键）。
    /// </summary>
    public static GameLaunchProfile? GetLaunchProfileById(GameBiz biz, string? id)
    {
        id = GameLaunchProfile.NormalizeId(id);
        if (string.IsNullOrEmpty(id) || GameLaunchProfile.IsNoneId(id) || GameLaunchProfile.IsDefaultId(id))
        {
            return null;
        }
        foreach (GameLaunchProfile p in GetExtraLaunchProfiles(biz))
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// 保存指定游戏的额外启动配置文件列表（不含默认配置文件）。
    /// </summary>
    public static void SetExtraLaunchProfiles(GameBiz biz, List<GameLaunchProfile> profiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(profiles ?? new List<GameLaunchProfile>(), JsonSerializerOptions);
            SetValue(json, $"launch_profiles_extra_{biz}");
        }
        catch { }
    }

    /// <summary>
    /// 获取 config1 的自定义显示名（为 null 时由界面按序号生成为「配置文件1」）。
    /// </summary>
    public static string? GetDefaultLaunchProfileName(GameBiz biz)
    {
        return GetValue<string>(default, $"launch_profile_default_name_{biz}");
    }

    /// <summary>
    /// 设置 config1 的自定义显示名。
    /// </summary>
    public static void SetDefaultLaunchProfileName(GameBiz biz, string? value)
    {
        SetValue(value, $"launch_profile_default_name_{biz}");
    }

    /// <summary>
    /// 获取 config1 绑定的登录账号游戏 UID（米游社工具箱角色）；0 表示不指定。
    /// </summary>
    public static long GetDefaultLaunchLoginUid(GameBiz biz)
    {
        return GetValue<long>(0, $"launch_profile_login_uid_{biz}");
    }

    /// <summary>
    /// 设置 config1 绑定的登录账号游戏 UID；传入 null 或 ≤0 表示清除。
    /// </summary>
    public static void SetDefaultLaunchLoginUid(GameBiz biz, long? value)
    {
        long uid = value is > 0 ? value.Value : 0;
        SetValue<long?>(uid == 0 ? null : uid, $"launch_profile_login_uid_{biz}");
    }


    /// <summary>
    /// 获取 config1 是否跳过启动时自动附加的 <c>-use-d3d12</c>。
    /// </summary>
    public static bool GetDefaultLaunchProfileSkipAutoDx12(GameBiz biz)
    {
        return GetValue<bool>(false, $"launch_profile_skip_auto_dx12_{biz}");
    }


    /// <summary>
    /// 设置 config1 是否跳过启动时自动附加的 <c>-use-d3d12</c>。
    /// </summary>
    public static void SetDefaultLaunchProfileSkipAutoDx12(GameBiz biz, bool value)
    {
        SetValue(value, $"launch_profile_skip_auto_dx12_{biz}");
    }

    /// <summary>
    /// 获取当前在启动参数编辑界面选中的配置文件内部名（configN）。
    /// </summary>
    public static string? GetSelectedLaunchProfileId(GameBiz biz)
    {
        string? id = GetValue<string>(default, $"launch_profile_selected_{biz}");
        id = GameLaunchProfile.NormalizeId(id);
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// 设置当前在启动参数编辑界面选中的配置文件内部名。
    /// </summary>
    public static void SetSelectedLaunchProfileId(GameBiz biz, string? value)
    {
        string? id = GameLaunchProfile.NormalizeId(value);
        SetValue(string.IsNullOrEmpty(id) ? null : id, $"launch_profile_selected_{biz}");
    }


    /// <summary>
    /// 获取当前生效（active）的启动方式内部名：点击「开始游戏」、以及不带 profile 参数的
    /// <c>moonward://startgame/{biz}</c>（「跟随软件设置」）均按此启动。
    /// 与编辑界面用的 <see cref="GetSelectedLaunchProfileId"/> 区分。
    /// 未设置、none 或无效 id 时返回 <see cref="GameLaunchProfile.NoneId"/>。
    /// </summary>
    public static string GetActiveLaunchProfileId(GameBiz biz)
    {
        string? raw = GetValue<string>(default, $"launch_profile_active_{biz}");
        if (GameLaunchProfile.IsNoneId(raw))
        {
            return GameLaunchProfile.NoneId;
        }
        string id = GameLaunchProfile.NormalizeId(raw);
        return string.IsNullOrEmpty(id) ? GameLaunchProfile.NoneId : id;
    }

    /// <summary>
    /// 设置当前生效（active）的启动方式内部名（「选择启动方式」点击「应用」后写入）。
    /// 传入空或 <see cref="GameLaunchProfile.NoneId"/> 表示「无」。
    /// </summary>
    public static void SetActiveLaunchProfileId(GameBiz biz, string? value)
    {
        if (GameLaunchProfile.IsNoneId(value))
        {
            SetValue(GameLaunchProfile.NoneId, $"launch_profile_active_{biz}");
            return;
        }
        string id = GameLaunchProfile.NormalizeId(value);
        SetValue(string.IsNullOrEmpty(id) ? GameLaunchProfile.NoneId : id, $"launch_profile_active_{biz}");
    }


    /// <summary>
    /// 解析启动方式：是否为「无」、以及对应的配置文件（config1 时 profile 为 null，由调用方读 legacy 键）。
    /// </summary>
    /// <param name="biz">游戏区服。</param>
    /// <param name="profileId">启动方式 / 配置内部名；null 或 none 表示「无」。</param>
    /// <param name="useNoneLaunchMethod">为 true 时不使用任何启动参数配置（仍可应用 DX12 等全局开关）。</param>
    /// <param name="profile">额外配置文件；config1 或「无」时为 null。</param>
    public static void ResolveLaunchProfile(GameBiz biz, string? profileId, out bool useNoneLaunchMethod, out GameLaunchProfile? profile)
    {
        profile = null;
        if (GameLaunchProfile.IsNoneId(profileId))
        {
            useNoneLaunchMethod = true;
            return;
        }
        useNoneLaunchMethod = false;
        if (GameLaunchProfile.IsDefaultId(profileId))
        {
            return;
        }
        profile = GetLaunchProfileById(biz, profileId);
    }


    /// <summary>
    /// 获取指定游戏是否使用无边框（Popup）窗口模式。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    public static bool GetUsePopupWindow(GameBiz biz)
    {
        return GetValue<bool>(false, $"use_popup_window_{biz}");
    }

    /// <summary>
    /// 设置指定游戏是否使用无边框（Popup）窗口模式。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">是否启用。</param>
    public static void SetUsePopupWindow(GameBiz biz, bool value)
    {
        SetValue(value, $"use_popup_window_{biz}");
    }


    /// <summary>
    /// 获取指定游戏在抽卡记录页面最后查看的 UID。
    /// </summary>
    public static long GetLastUidInGachaLogPage(GameBiz biz)
    {
        return GetValue<long>(default, $"last_gacha_uid_{biz}");
    }

    /// <summary>
    /// 保存指定游戏在抽卡记录页面最后查看的 UID。
    /// </summary>
    public static void SetLastUidInGachaLogPage(GameBiz biz, long value)
    {
        SetValue(value, $"last_gacha_uid_{biz}");
    }


    [Obsolete("已不用")]
    public static GameBiz GetLastRegionOfGame(GameBiz game)
    {
        return GetValue<GameBiz>(default, $"last_region_of_{game}");
    }

    [Obsolete("已不用")]
    public static void SetLastRegionOfGame(GameBiz game, GameBiz value)
    {
        SetValue(value, $"last_region_of_{game}");
    }

    //记住用户在这个游戏里“选择了哪些卡池来显示统计”。
    /// <summary>
    /// 获取指定游戏在抽卡统计页面显示哪些卡池的逗号分隔字符串。
    /// </summary>
    public static string? GetDisplayGachaBanners(GameBiz biz)
    {
        return GetValue<string>(default, $"display_gacha_banners_{biz}");
    }

    public static void SetDisplayGachaBanners(GameBiz biz, string value)
    {
        SetValue(value, $"display_gacha_banners_{biz}");
    }


    /// <summary>
    /// 抽卡统计卡片的自定义排列次序（卡池类型逗号串），按游戏持久化；拖拽换位后保存，刷新数据后据此还原相对位置。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    public static string? GetGachaCardOrder(GameBiz biz)
    {
        return GetValue<string>(default, $"gacha_card_order_{biz}");
    }

    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">卡池类型逗号串。</param>
    public static void SetGachaCardOrder(GameBiz biz, string value)
    {
        SetValue(value, $"gacha_card_order_{biz}");
    }


    /// <summary>
    /// 获取指定游戏的外部截图文件夹路径。
    /// </summary>
    public static string? GetExternalScreenshotFolder(GameBiz biz)
    {
        return GetValue<string>(default, $"external_screenshot_folder_{biz}");
    }

    /// <summary>
    /// 设置指定游戏的外部截图文件夹路径。
    /// </summary>
    public static void SetExternalScreenshotFolder(GameBiz biz, string? value)
    {
        SetValue(value, $"external_screenshot_folder_{biz}");
    }


    public static string? GetGameBackgroundIds(GameBiz biz)
    {
        return GetValue<string>(default, $"game_background_ids_{biz}");
    }

    public static void SetGameBackgroundIds(GameBiz biz, string? value)
    {
        SetValue(value, $"game_background_ids_{biz}");
    }


    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static bool GetEnableDX12(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_dx12_{biz}");
    }

    /// <summary>
    /// 启用 DX12
    /// </summary>
    public static void SetEnableDX12(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_dx12_{biz}");
    }


    /// <summary>
    /// 获取指定游戏的背景视频音量（0-100）。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    public static int GetVideoBgVolume(GameBiz biz)
    {
        return Math.Clamp(GetValue(0, $"video_bg_volume_{biz}"), 0, 100);
    }

    /// <summary>
    /// 设置指定游戏的背景视频音量（0-100）。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">音量值。</param>
    public static void SetVideoBgVolume(GameBiz biz, int value)
    {
        SetValue(value, $"video_bg_volume_{biz}");
    }


    #endregion



    #region Setting Method（设置读写核心实现）


    /// <summary>
    /// 内存中的设置缓存（Key → Value 字符串）。
    /// </summary>
    private static Dictionary<string, string?> _settingCache;


    /// <summary>
    /// 初始化设置缓存（从数据库 Setting 表一次性加载所有键值对）。
    /// </summary>
    private static void InitializeSettingProvider()
    {
        try
        {
            if (_settingCache is null)
            {
                using var dapper = DatabaseService.CreateConnection();
                _settingCache = dapper.Query<(string Key, string? Value)>("SELECT Key, Value FROM Setting;").ToDictionary(x => x.Key, x => x.Value);
            }
        }
        catch { }
    }


    /// <summary>
    /// 获取指定 Key 的设置值（泛型自动转换）。
    /// 优先从内存缓存读取，未命中时从数据库读取并回填缓存。
    /// </summary>
    /// <typeparam name="T">目标类型（通过 TypeConverter 转换）。</typeparam>
    /// <param name="defaultValue">当 Key 不存在或转换失败时返回的默认值。</param>
    /// <param name="key">设置键名。通常由 [CallerMemberName] 自动传入属性名，也可手动指定（用于 per-game 动态设置）。</param>
    /// <returns>转换后的值或 defaultValue。</returns>
    public static T? GetValue<T>(T? defaultValue = default, [CallerMemberName] string? key = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return defaultValue;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return defaultValue;
        }
        try
        {
            if (_settingCache.TryGetValue(key, out string? value))
            {
                return ConvertFromString(value, defaultValue);
            }
            using var dapper = DatabaseService.CreateConnection();
            value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key=@key LIMIT 1;", new { key });
            _settingCache[key] = value;
            return ConvertFromString(value, defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }


    /// <summary>
    /// 将字符串值通过 TypeConverter 转换为目标类型 T。
    /// </summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="value">数据库/缓存中的字符串值。</param>
    /// <param name="defaultValue">转换失败时的回退值。</param>
    private static T? ConvertFromString<T>(string? value, T? defaultValue = default)
    {
        if (value is null)
        {
            return defaultValue;
        }
        var converter = TypeDescriptor.GetConverter(typeof(T));
        if (converter == null)
        {
            return defaultValue;
        }
        return (T?)converter.ConvertFromString(value);
    }


    /// <summary>
    /// 设置指定 Key 的值（会写入数据库并更新内存缓存）。
    /// 如果新值与缓存中的值相同则跳过写入。
    /// </summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="value">要保存的值（会调用 ToString() 持久化）。</param>
    /// <param name="key">设置键名（通常由 CallerMemberName 提供）。</param>
    public static void SetValue<T>(T? value, [CallerMemberName] string? key = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return;
        }
        try
        {
            string? val = value?.ToString();
            if (_settingCache.TryGetValue(key, out string? cacheValue) && cacheValue == val)
            {
                return;
            }
            _settingCache[key] = val;
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @val);", new { key, val });
        }
        catch { }
    }


    /// <summary>
    /// 删除 Setting 表中所有记录（危险操作，主要用于重置或测试）。
    /// </summary>
    public static void DeleteAllSettings()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("DELETE FROM Setting WHERE TRUE;");
        }
        catch { }
    }


    /// <summary>
    /// 清空内存设置缓存（下次 GetValue 时会重新从数据库加载）。
    /// </summary>
    public static void ClearCache()
    {
        _settingCache.Clear();
    }


    #endregion


}
