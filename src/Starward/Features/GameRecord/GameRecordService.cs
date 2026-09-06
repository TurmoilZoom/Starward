using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.Gacha.ZZZ;
using Starward.Core.GameRecord;
using Starward.Core.GameRecord.BH3.DailyNote;
using Starward.Core.GameRecord.Genshin.DailyNote;
using Starward.Core.GameRecord.Genshin.ImaginariumTheater;
using Starward.Core.GameRecord.Genshin.SpiralAbyss;
using Starward.Core.GameRecord.Genshin.StygianOnslaught;
using Starward.Core.GameRecord.Genshin.TravelersDiary;
using Starward.Core.GameRecord.StarRail.ApocalypticShadow;
using Starward.Core.GameRecord.StarRail.ChallengePeak;
using Starward.Core.GameRecord.StarRail.DailyNote;
using Starward.Core.GameRecord.StarRail.ForgottenHall;
using Starward.Core.GameRecord.StarRail.PureFiction;
using Starward.Core.GameRecord.StarRail.SimulatedUniverse;
using Starward.Core.GameRecord.StarRail.TrailblazeCalendar;
using Starward.Core.GameRecord.ZZZ.DailyNote;
using Starward.Core.GameRecord.ZZZ.DeadlyAssault;
using Starward.Core.GameRecord.ZZZ.GachaRecord;
using Starward.Core.GameRecord.SignIn;
using Starward.Core.GameRecord.ZZZ.InterKnotReport;
using Starward.Core.GameRecord.ZZZ.ShiyuDefense;
using Starward.Core.GameRecord.Passport;
using Starward.Features.Database;
using Starward.Features.ViewHost;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord;

internal class GameRecordService
{

    /// <summary>
    /// 国服请求失败后刷新设备指纹的最小间隔，避免连续业务错误频繁请求 public-data-api。
    /// </summary>
    private static readonly TimeSpan DeviceFpFailureUpdateCooldown = TimeSpan.FromHours(6);

    /// <summary>
    /// 当前 getFp ext_fields 载荷版本。旧版本含 windows 硬件字段，升级后必须强制重刷指纹。
    /// </summary>
    private const int DeviceFpPayloadVersion = 2;

    private readonly ILogger<GameRecordService> _logger;

    private readonly HyperionClient _hyperionClient;

    private readonly HoyolabClient _hoyolabClient;

    private GameRecordClient _gameRecordClient;

    private readonly GameRecordCookieRefreshService _cookieRefreshService;

    /// <summary>
    /// 串行化所有国服设备指纹更新，确保冷却检查与刷新请求不会并发执行。
    /// </summary>
    private readonly SemaphoreSlim _deviceFpUpdateLock = new(1, 1);


    private readonly IMemoryCache _memoryCache;


    public string Language { get => _hoyolabClient.Language; set => _hoyolabClient.Language = value; }


    private bool isHoyolab;
    public bool IsHoyolab
    {
        get => isHoyolab;
        set
        {
            if (value)
            {
                _gameRecordClient = _hoyolabClient;
            }
            else
            {
                _gameRecordClient = _hyperionClient;
            }
            isHoyolab = value;
        }
    }


    /// <summary>
    /// 初始化 GameRecord 门面及 Cookie 静默刷新依赖。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="hyperionClient">国服 GameRecord Client。</param>
    /// <param name="hoyolabClient">国际服 GameRecord Client。</param>
    /// <param name="memoryCache">战绩与头像等数据的内存缓存。</param>
    /// <param name="cookieRefreshService">国服 Cookie（stoken 换票）刷新协调器。</param>
    public GameRecordService(ILogger<GameRecordService> logger, HyperionClient hyperionClient, HoyolabClient hoyolabClient, IMemoryCache memoryCache, GameRecordCookieRefreshService cookieRefreshService)
    {
        _logger = logger;
        _hyperionClient = hyperionClient;
        _hoyolabClient = hoyolabClient;
        _gameRecordClient = hyperionClient;
        _memoryCache = memoryCache;
        _cookieRefreshService = cookieRefreshService;
    }




    /// <summary>
    /// 按角色区服选择固定的 GameRecord Client，避免并发请求受页面当前平台状态影响。
    /// </summary>
    /// <param name="role">用于判断国服或国际服的游戏角色。</param>
    /// <returns>角色对应平台的 Client。</returns>
    private GameRecordClient GetClient(GameRecordRole role)
    {
        return IsGlobalServerRole(role) ? _hoyolabClient : _hyperionClient;
    }


    /// <summary>
    /// 角色是否属于国际服（HoYoLAB）。按角色自身判断，不读共享的 <see cref="IsHoyolab"/>。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <returns>国际服为 true。</returns>
    private static bool IsGlobalServerRole(GameRecordRole role)
    {
        return role.GameBiz?.EndsWith("_global", StringComparison.OrdinalIgnoreCase) ?? false;
    }


    /// <summary>
    /// 执行角色 GameRecord 请求；国服接口失败时先刷新设备指纹，登录失效时再凭 stoken 刷新 Cookie，并仅重试一次。
    /// </summary>
    /// <typeparam name="T">请求返回类型。</typeparam>
    /// <param name="role">请求使用的游戏角色，刷新成功后会原地更新其 Cookie。</param>
    /// <param name="action">使用已选平台 Client 发起请求的委托。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首次请求或一次恢复重试后的结果。</returns>
    private async Task<T> ExecuteWithRequestRecoveryAsync<T>(GameRecordRole role, Func<GameRecordClient, Task<T>> action, CancellationToken cancellationToken = default)
    {
        EnsureCookiePresent(role.Cookie);
        GameRecordClient client = GetClient(role);
        string failedDeviceFp = _hyperionClient.DeviceFp;
        try
        {
            return await action(client);
        }
        catch (miHoYoApiException ex)
        {
            var aigisRetry = await TryRetryAfterAigisAsync(client, ex, action, cancellationToken);
            if (aigisRetry.Handled)
            {
                return aigisRetry.Result;
            }

            if (client is not HyperionClient)
            {
                throw;
            }

            bool deviceFpUpdated = await TryUpdateDeviceFpAfterRequestFailureAsync(failedDeviceFp, ex, cancellationToken);

            if (ex.IsLoginExpired)
            {
                string? refreshedCookie = await _cookieRefreshService.RefreshCookieAsync(role, cancellationToken);
                if (string.IsNullOrWhiteSpace(refreshedCookie))
                {
                    throw;
                }
            }
            else if (!deviceFpUpdated)
            {
                // 未更新指纹时重试相同请求没有恢复条件，直接保留首次业务错误。
                throw;
            }

            try
            {
                return await action(client);
            }
            catch (miHoYoApiException retryException) when (ex.IsLoginExpired && retryException.IsLoginExpired)
            {
                // 二次鉴权失败时保留首次异常的调用栈与原始接口信息。
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }


    /// <summary>
    /// 执行账号 Cookie 请求；国服接口失败时先刷新设备指纹，登录失效时再凭 stoken 刷新 Cookie，并仅重试一次。
    /// </summary>
    /// <typeparam name="T">请求返回类型。</typeparam>
    /// <param name="cookie">验证码登录或手动输入的完整 Cookie。</param>
    /// <param name="isHoyolab">是否为国际服；国际服不尝试 Token 交换。</param>
    /// <param name="action">接收平台 Client 与当前 Cookie 并发起请求的委托。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首次请求或一次恢复重试后的结果。</returns>
    private async Task<T> ExecuteWithRequestRecoveryAsync<T>(string cookie, bool isHoyolab, Func<GameRecordClient, string, Task<T>> action, CancellationToken cancellationToken = default)
    {
        EnsureCookiePresent(cookie);
        GameRecordClient client = isHoyolab ? _hoyolabClient : _hyperionClient;
        string failedDeviceFp = _hyperionClient.DeviceFp;
        try
        {
            return await action(client, cookie);
        }
        catch (miHoYoApiException ex)
        {
            var aigisRetry = await TryRetryAfterAigisAsync(client, ex, c => action(c, cookie), cancellationToken);
            if (aigisRetry.Handled)
            {
                return aigisRetry.Result;
            }

            if (isHoyolab)
            {
                throw;
            }

            bool deviceFpUpdated = await TryUpdateDeviceFpAfterRequestFailureAsync(failedDeviceFp, ex, cancellationToken);

            string currentCookie = cookie;
            if (ex.IsLoginExpired)
            {
                string? refreshedCookie = await _cookieRefreshService.RefreshCookieAsync(cookie, cancellationToken);
                if (string.IsNullOrWhiteSpace(refreshedCookie))
                {
                    throw;
                }
                currentCookie = refreshedCookie;
            }
            else if (!deviceFpUpdated)
            {
                // 未更新指纹时重试相同请求没有恢复条件，直接保留首次业务错误。
                throw;
            }

            try
            {
                return await action(client, currentCookie);
            }
            catch (miHoYoApiException retryException) when (ex.IsLoginExpired && retryException.IsLoginExpired)
            {
                // 二次鉴权失败时保留首次异常的调用栈与原始接口信息。
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }



    /// <summary>
    /// 若业务错误带有 <c>x-rpc-aigis</c>，弹出极验并带挑战头重试一次。
    /// </summary>
    private async Task<(bool Handled, T Result)> TryRetryAfterAigisAsync<T>(GameRecordClient client, miHoYoApiException ex, Func<GameRecordClient, Task<T>> action, CancellationToken cancellationToken)
    {
        if (ex.Aigis is null || string.IsNullOrWhiteSpace(ex.Aigis.Data))
        {
            return (false, default!);
        }

        string? aigisHeader = await ResolveGameRecordAigisAsync(ex.Aigis, cancellationToken);
        if (string.IsNullOrWhiteSpace(aigisHeader))
        {
            return (false, default!);
        }

        ApplyGameRecordRiskHeaders(client, aigisHeader);
        try
        {
            T result = await action(client);
            return (true, result);
        }
        finally
        {
            client.RiskAigisHeader = null;
            client.RiskChallenge = null;
        }
    }


    /// <summary>
    /// 在主窗口弹出极验。无法取得 UI 时返回 null，由上层继续走「验证账号」WebView。
    /// </summary>
    private async Task<string?> ResolveGameRecordAigisAsync(CaptchaAigis aigis, CancellationToken cancellationToken)
    {
        MainWindow? window = MainWindow.Current;
        if (window?.Content?.XamlRoot is not { } xamlRoot)
        {
            _logger.LogWarning("Cannot show game-record geetest: MainWindow XamlRoot is unavailable.");
            return null;
        }

        if (window.DispatcherQueue.HasThreadAccess)
        {
            return await GeetestVerifyPopup.ShowAsync(xamlRoot, aigis, cancellationToken);
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await GeetestVerifyPopup.ShowAsync(xamlRoot, aigis, cancellationToken));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            return null;
        }

        return await tcs.Task.WaitAsync(cancellationToken);
    }


    /// <summary>
    /// 把极验结果写到 Client，供下一次 CommonSendAsync 带上 aigis / challenge。
    /// </summary>
    private static void ApplyGameRecordRiskHeaders(GameRecordClient client, string aigisHeader)
    {
        client.RiskAigisHeader = aigisHeader;
        client.RiskChallenge = TryReadGeetestChallenge(aigisHeader);
    }


    /// <summary>
    /// 从 <c>session_id;base64(validateJson)</c> 取出 geetest_challenge，供 <c>x-rpc-challenge</c> 使用。
    /// </summary>
    private static string? TryReadGeetestChallenge(string aigisHeader)
    {
        try
        {
            int separator = aigisHeader.IndexOf(';');
            if (separator < 0 || separator >= aigisHeader.Length - 1)
            {
                return null;
            }
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(aigisHeader[(separator + 1)..]));
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("geetest_challenge", out JsonElement challenge))
            {
                return challenge.GetString();
            }
            if (doc.RootElement.TryGetProperty("challenge", out JsonElement challengeAlt))
            {
                return challengeAlt.GetString();
            }
        }
        catch
        {
            // 校验 JSON 非预期时只带 aigis 头重试
        }
        return null;
    }


    /// <summary>
    /// 在国服接口返回业务错误后按冷却策略刷新设备指纹；指纹刷新失败时保留最初的业务错误。
    /// </summary>
    /// <param name="failedDeviceFp">触发失败请求使用的设备指纹，用于并发去重。</param>
    /// <param name="originalException">触发恢复流程的原始米哈游接口异常。</param>
    /// <param name="cancellationToken">取消令牌；调用方取消时应立即停止恢复流程。</param>
    /// <returns>设备指纹已更新或已由并发请求更新时返回 true；处于冷却期而跳过时返回 false。</returns>
    private async Task<bool> TryUpdateDeviceFpAfterRequestFailureAsync(string failedDeviceFp, miHoYoApiException originalException, CancellationToken cancellationToken)
    {
        try
        {
            await _deviceFpUpdateLock.WaitAsync(cancellationToken);
            try
            {
                // 指纹已被其他失败请求更新时直接复用，避免并发请求重复触发 public-data-api。
                if (!string.Equals(_hyperionClient.DeviceFp, failedDeviceFp, StringComparison.Ordinal))
                {
                    return true;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset lastUpdateTime = AppConfig.HyperionDeviceFpLastUpdateTime;
                DateTimeOffset lastFailureAttemptTime = AppConfig.HyperionDeviceFpLastFailureUpdateAttemptTime;
                DateTimeOffset lastAttemptTime = lastUpdateTime > lastFailureAttemptTime ? lastUpdateTime : lastFailureAttemptTime;
                if (now - lastAttemptTime < DeviceFpFailureUpdateCooldown)
                {
                    _logger.LogDebug(
                        "Skipped Hyperion device fingerprint refresh after API retcode {returnCode}; the {cooldown} cooldown has not elapsed.",
                        originalException.ReturnCode,
                        DeviceFpFailureUpdateCooldown);
                    return false;
                }

                // 在请求前持久化尝试时间，避免接口异常或应用重启后立即再次触发刷新。
                AppConfig.HyperionDeviceFpLastFailureUpdateAttemptTime = now;
                await UpdateHyperionDeviceFpAsync(true, cancellationToken);
                return true;
            }
            finally
            {
                _deviceFpUpdateLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 恢复动作失败不能覆盖用户真正遇到的 API 业务错误。
            _logger.LogWarning(ex, "Failed to update the Hyperion device fingerprint after API retcode {returnCode}.", originalException.ReturnCode);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(originalException).Throw();
            throw;
        }
    }



    /// <summary>
    /// 更新当前米游社工具箱使用的设备指纹信息。
    /// </summary>
    /// <param name="forceUpdate">是否忽略三天更新间隔并强制请求新的设备指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示设备指纹已载入或更新完成的任务。</returns>
    public async Task UpdateDeviceFpAsync(bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        if (IsHoyolab)
        {
            return;
        }
        await UpdateHyperionDeviceFpWithLockAsync(forceUpdate, cancellationToken);
    }


    /// <summary>
    /// 更新国服设备指纹。调用方已确定是国服场景（国服接口、国服登录）时用它，
    /// 不受共享的 <see cref="IsHoyolab"/> 影响——别处把那个字段置为国际服时，
    /// <see cref="UpdateDeviceFpAsync"/> 会静默跳过，导致国服请求带着空的 / 过期的指纹。
    /// </summary>
    /// <param name="forceUpdate">是否忽略三天更新间隔并强制请求新的设备指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task EnsureHyperionDeviceFpAsync(bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        return UpdateHyperionDeviceFpWithLockAsync(forceUpdate, cancellationToken);
    }


    /// <summary>
    /// 更新国服设备指纹；由调用方确认是国服角色，不读共享的 <see cref="IsHoyolab"/>。
    /// 按角色发起的请求（签到、抽卡等）用它：这些请求本就按角色选 Client，
    /// 不该为了走指纹分支去改全局字段——后台线程的改动会串到界面正在进行的账号操作上。
    /// </summary>
    /// <param name="forceUpdate">是否忽略三天更新间隔并强制请求新的设备指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task UpdateHyperionDeviceFpWithLockAsync(bool forceUpdate, CancellationToken cancellationToken)
    {
        await _deviceFpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            await UpdateHyperionDeviceFpAsync(forceUpdate, cancellationToken);
        }
        finally
        {
            _deviceFpUpdateLock.Release();
        }
    }



    /// <summary>
    /// 使用国服 Hyperion Client 载入或更新设备指纹，并将最新值持久化到应用设置。
    /// </summary>
    /// <param name="forceUpdate">是否忽略三天更新间隔并强制请求新的设备指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示设备指纹已载入或更新完成的任务。</returns>
    private async Task UpdateHyperionDeviceFpAsync(bool forceUpdate, CancellationToken cancellationToken)
    {
        string? id = AppConfig.HyperionDeviceId;
        string? fp = AppConfig.HyperionDeviceFp;
        DateTimeOffset lastUpdateTime = AppConfig.HyperionDeviceFpLastUpdateTime;
        if (!string.IsNullOrWhiteSpace(id))
        {
            _hyperionClient.DeviceId = id;
        }
        if (!string.IsNullOrWhiteSpace(fp))
        {
            _hyperionClient.DeviceFp = fp;
        }
        if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFpSeedId))
        {
            _hyperionClient.DeviceFpSeedId = AppConfig.HyperionDeviceFpSeedId;
        }
        if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFpSeedTime))
        {
            _hyperionClient.DeviceFpSeedTime = AppConfig.HyperionDeviceFpSeedTime;
        }
        if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceAndroidId))
        {
            _hyperionClient.DeviceAndroidId = AppConfig.HyperionDeviceAndroidId;
        }

        // 旧指纹 ext_fields 带 windows 硬件信息，绝区零战绩会直接 10041，必须换一套 Android 载荷。
        if (AppConfig.HyperionDeviceFpPayloadVersion < DeviceFpPayloadVersion)
        {
            forceUpdate = true;
        }

        if (forceUpdate || DateTimeOffset.Now - lastUpdateTime > TimeSpan.FromDays(3))
        {
            await _hyperionClient.GetDeviceFpAsync(cancellationToken);
            AppConfig.HyperionDeviceId = _hyperionClient.DeviceId;
            AppConfig.HyperionDeviceFp = _hyperionClient.DeviceFp;
            AppConfig.HyperionDeviceFpSeedId = _hyperionClient.DeviceFpSeedId;
            AppConfig.HyperionDeviceFpSeedTime = _hyperionClient.DeviceFpSeedTime;
            AppConfig.HyperionDeviceAndroidId = _hyperionClient.DeviceAndroidId;
            AppConfig.HyperionDeviceFpPayloadVersion = DeviceFpPayloadVersion;
            AppConfig.HyperionDeviceFpLastUpdateTime = DateTimeOffset.Now;
        }
    }


    /// <summary>
    /// 空 Cookie 写入 HTTP 头会抛 <c>FormatException</c>（value '&lt;null&gt;'），改成登录失效以便 UI 引导重新登录。
    /// </summary>
    private static void EnsureCookiePresent(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new miHoYoApiException(-100, "Cookie is empty.");
        }
    }


    private static bool HasCookie(GameRecordRole? role) => !string.IsNullOrWhiteSpace(role?.Cookie);


    private static bool HasCookie(GameRecordUser? user) => !string.IsNullOrWhiteSpace(user?.Cookie);



    #region Game Role



    /// <summary>
    /// 用 Cookie 拉取米游社 / HoYoLAB 账号信息并入库。
    /// </summary>
    /// <param name="cookie">登录得到的完整 Cookie。</param>
    /// <param name="isHoyolab">是否国际服。由调用方按本次登录的区服显式传入，不读共享的 <see cref="IsHoyolab"/>：
    /// 那个字段会被别的界面与后台任务改动，读它可能把国服登录发到国际服接口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>入库后的账号信息。</returns>
    public async Task<GameRecordUser> AddRecordUserAsync(string cookie, bool isHoyolab, CancellationToken cancellationToken = default)
    {
        var user = await ExecuteWithRequestRecoveryAsync(cookie, isHoyolab, (client, currentCookie) => client.GetGameRecordUserAsync(currentCookie, cancellationToken), cancellationToken);
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GameRecordUser (Uid, IsHoyolab, Nickname, Avatar, Introduce, Gender, AvatarUrl, Pendant, Cookie)
            VALUES (@Uid, @IsHoyolab, @Nickname, @Avatar, @Introduce, @Gender, @AvatarUrl, @Pendant, @Cookie);
            """, user);
        return user;
    }



    /// <summary>
    /// 读取指定平台下已登录的账号。
    /// </summary>
    /// <param name="isHoyolab">是否国际服，理由见 <see cref="AddRecordUserAsync"/>。</param>
    /// <returns>该平台下带 Cookie 的账号。</returns>
    public List<GameRecordUser> GetRecordUsers(bool isHoyolab)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordUser>("SELECT * FROM GameRecordUser WHERE IsHoyolab = @isHoyolab;", new { isHoyolab });
        return list.Where(HasCookie).ToList();
    }



    /// <summary>
    /// 用 Cookie 拉取该账号下全部游戏角色并入库。
    /// </summary>
    /// <param name="cookie">登录得到的完整 Cookie。</param>
    /// <param name="isHoyolab">是否国际服，理由见 <see cref="AddRecordUserAsync"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>入库后的角色列表。</returns>
    public async Task<List<GameRecordRole>> AddGameRolesAsync(string cookie, bool isHoyolab, CancellationToken cancellationToken = default)
    {
        var list = await ExecuteWithRequestRecoveryAsync(cookie, isHoyolab, (client, currentCookie) => client.GetAllGameRolesAsync(currentCookie, cancellationToken), cancellationToken);
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        dapper.Execute("""
            INSERT OR REPLACE INTO GameRecordRole (Uid, GameBiz, Nickname, Level, Region, RegionName, Cookie, HeadIcon)
            VALUES (@Uid, @GameBiz, @Nickname, @Level, @Region, @RegionName, @Cookie, @HeadIcon);
            """, list, t);
        t.Commit();
        return list;
    }




    public List<GameRecordRole> GetGameRoles(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz;", new { gameBiz });
        return list.Where(HasCookie).ToList();
    }



    /// <summary>
    /// 数据库中全部游戏角色（跨所有账号 cookie 与游戏），按账号(cookie)再按游戏排序，供自动签到批量遍历。
    /// </summary>
    public List<GameRecordRole> GetAllGameRoles()
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole ORDER BY Cookie, GameBiz;");
        return list.Where(HasCookie).ToList();
    }



    public GameRecordRole? GetLastSelectGameRecordRoleOrTheFirstOne(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        var role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_game_record_role_{gameBiz}" });
        if (role is not null && HasCookie(role))
        {
            return role;
        }
        return dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz;", new { gameBiz })
            .FirstOrDefault(HasCookie);
    }



    public void SetLastSelectGameRecordRole(GameBiz gameBiz, GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_game_record_role_{gameBiz}", value = role.Uid.ToString() });
    }


    public GameRecordRole? GetLastSelectGachaSyncRoleOrTheFirstOne(GameBiz gameBiz)
    {
        using var dapper = DatabaseService.CreateConnection();
        GameRecordRole? role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_gacha_sync_role_{gameBiz}" });
        if (role is not null && HasCookie(role))
        {
            return role;
        }
        role = dapper.QueryFirstOrDefault<GameRecordRole>("""
            SELECT r.* FROM GameRecordRole r INNER JOIN Setting s ON s.Value = r.Uid WHERE r.GameBiz = @gameBiz AND s.Key = @key LIMIT 1;
            """, new { gameBiz, key = $"last_select_game_record_role_{gameBiz}" });
        if (role is not null && HasCookie(role))
        {
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_gacha_sync_role_{gameBiz}", value = role.Uid.ToString() });
            return role;
        }
        return dapper.Query<GameRecordRole>("SELECT * FROM GameRecordRole WHERE GameBiz = @gameBiz;", new { gameBiz })
            .FirstOrDefault(HasCookie);
    }


    public void SetLastSelectGachaSyncRole(GameBiz gameBiz, GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key = $"last_select_gacha_sync_role_{gameBiz}", value = role.Uid.ToString() });
    }


    public GameRecordUser? GetGameRecordUser(GameRecordRole? role)
    {
        if (role is null)
        {
            return null;
        }
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<GameRecordUser>("SELECT * FROM GameRecordUser WHERE Cookie = @Cookie LIMIT 1;", new { role.Cookie });
    }



    /// <summary>
    /// 刷新指定平台下全部账号的角色信息。
    /// </summary>
    /// <param name="isHoyolab">是否国际服，理由见 <see cref="AddRecordUserAsync"/>。</param>
    public async Task RefreshAllGameRolesInfoAsync(bool isHoyolab)
    {
        var users = GetRecordUsers(isHoyolab);
        foreach (var user in users)
        {
            await AddRecordUserAsync(user.Cookie!, isHoyolab);
            await AddGameRolesAsync(user.Cookie!, isHoyolab);
        }
    }


    /// <summary>
    /// 刷新单个角色所属账号的信息，平台按该角色判断。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    public async Task RefreshGameRoleInfoAsync(GameRecordRole role)
    {
        bool isHoyolab = IsGlobalServerRole(role);
        await AddRecordUserAsync(role.Cookie!, isHoyolab);
        await AddGameRolesAsync(role.Cookie!, isHoyolab);
    }



    public async Task UpdateGameRoleHeadIconAsync(GameRecordRole role)
    {
        string key = $"game_record_role_head_icon_{role.GameBiz}_{role.Region}_{role.Uid}";
        if (!_memoryCache.TryGetValue(key, out bool _))
        {
            role = await ExecuteWithRequestRecoveryAsync(role, client => client.UpdateGameRoleHeadIconAsync(role));
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("""
                INSERT OR REPLACE INTO GameRecordRole (Uid, GameBiz, Nickname, Level, Region, RegionName, Cookie, HeadIcon)
                VALUES (@Uid, @GameBiz, @Nickname, @Level, @Region, @RegionName, @Cookie, @HeadIcon);
                """, role);
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(5));
        }
    }



    /// <summary>
    /// 删除游戏角色，返回是否删除全部账号
    /// </summary>
    /// <param name="role"></param>
    /// <returns></returns>
    public bool DeleteGameRole(GameRecordRole role)
    {
        bool deletedUser = false;
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        dapper.Execute("DELETE FROM GameRecordRole WHERE GameBiz = @GameBiz AND Uid = @Uid;", role, t);
        _logger.LogInformation("Deleted game roles with ({nickname}, {gameBiz}, {uid}).", role.Nickname, role.GameBiz, role.Uid);
        if (dapper.QueryFirstOrDefault<int>("SELECT Count(*) FROM GameRecordRole WHERE Cookie = @Cookie;", role, t) == 0)
        {
            dapper.Execute("DELETE FROM GameRecordUser WHERE Cookie = @Cookie;", role, t);
            _logger.LogInformation("Deleted all relative accounts of ({nickname}, {gameBiz}, {uid})", role.Nickname, role.GameBiz, role.Uid);
            deletedUser = true;
        }
        t.Commit();
        return deletedUser;
    }



    #endregion




    #region Spiral Abyss


    /// <summary>
    /// 深境螺旋
    /// </summary>
    /// <param name="role"></param>
    /// <param name="schedule">1当期，2上期</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SpiralAbyssInfo> RefreshSpiralAbyssInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetSpiralAbyssInfoAsync(role, schedule), cancellationToken);
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.StartTime,
            info.EndTime,
            info.TotalBattleCount,
            info.TotalWinCount,
            info.MaxFloor,
            info.TotalStar,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GenshinSpiralAbyssInfo (Uid, ScheduleId, StartTime, EndTime, TotalBattleCount, TotalWinCount, MaxFloor, TotalStar, Value)
            VALUES (@Uid, @ScheduleId, @StartTime, @EndTime, @TotalBattleCount, @TotalWinCount, @MaxFloor, @TotalStar, @Value);
            """, obj);
        return info;
    }




    public List<SpiralAbyssInfo> GetSpiralAbyssInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<SpiralAbyssInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<SpiralAbyssInfo>("""
            SELECT Uid, ScheduleId, StartTime, EndTime, TotalBattleCount, TotalWinCount, MaxFloor, TotalStar FROM GenshinSpiralAbyssInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public SpiralAbyssInfo? GetSpiralAbyssInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinSpiralAbyssInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var info = JsonSerializer.Deserialize<SpiralAbyssInfo>(value);
        if (info != null)
        {
            info.Floors = info.Floors.Where(x => x.Index > 8).OrderByDescending(x => x.Index).ToList();
        }
        return info;
    }


    #endregion




    #region Traveler's Diary



    public async Task<TravelersDiarySummary> GetTravelersDiarySummaryAsync(GameRecordRole role, int month = 0)
    {
        var summary = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTravelsDiarySummaryAsync(role, month));
        if (summary.MonthData is null)
        {
            return summary;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO GenshinTravelersDiaryMonthData
            (Uid, Year, Month, CurrentPrimogems, CurrentMora, LastPrimogems, LastMora, CurrentPrimogemsLevel, PrimogemsChangeRate, MoraChangeRate, PrimogemsGroupBy)
            VALUES (@Uid, @Year, @Month, @CurrentPrimogems, @CurrentMora, @LastPrimogems, @LastMora, @CurrentPrimogemsLevel, @PrimogemsChangeRate, @MoraChangeRate, @PrimogemsGroupBy);
            """, summary.MonthData);
        return summary;
    }


    public List<TravelersDiaryMonthData> GetTravelersDiaryMonthDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<TravelersDiaryMonthData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<TravelersDiaryMonthData>("SELECT * FROM GenshinTravelersDiaryMonthData WHERE Uid = @Uid ORDER BY Year DESC, Month DESC;", new { role.Uid });
        return list.ToList();
    }


    //原数据库使用的是自增id，在做增量更新时，逻辑判断比较复杂
    /// <param name="forceOverwrite">为 true 时先删除该 (uid, year, month, type) 的全部旧记录，再全量写入 API 返回值；false 时走增量逻辑。</param>
    public async Task<int> GetTravelersDiaryDetailAsync(GameRecordRole role, int month, int type, int limit = 100, bool forceOverwrite = false)
    {
        if (forceOverwrite)
        {
            // 全量覆盖：先删除旧数据，再批量写入 API 全部记录
            var fwDetail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTravelsDiaryDetailAsync(role, month, type, limit));
            var fwList = fwDetail.List;
            if (fwList.Count == 0)
            {
                return 0;
            }
            var fwFirstItem = fwList[0];
            using var fwDapper = DatabaseService.CreateConnection();
            using var fwTx = fwDapper.BeginTransaction();
            fwDapper.Execute("""
                DELETE FROM GenshinTravelersDiaryAwardItem
                WHERE Uid = @Uid AND Year = @Year AND Month = @Month AND Type = @Type;
                """, fwFirstItem, fwTx);
            fwDapper.Execute("""
                INSERT INTO GenshinTravelersDiaryAwardItem (Uid, Year, Month, Type, ActionId, ActionName, Time, Number)
                VALUES (@Uid, @Year, @Month, @Type, @ActionId, @ActionName, @Time, @Number);
                """, fwList, fwTx);
            fwTx.Commit();
            return fwList.Count;
        }

        // 探针请求：先获取第1页（limit=1）以同时得到总数和最新一条记录，避免冗余的全量查询
        var firstPage = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTravelsDiaryDetailByPageAsync(role, month, type, 1, 1));
        int total = firstPage.Total;
        if (total == 0)
        {
            return 0;
        }
        using var dapper = DatabaseService.CreateConnection();
        // 用 firstPage 第一条记录的元信息查询 DB 现有条数
        var firstItem = firstPage.List.FirstOrDefault();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM GenshinTravelersDiaryAwardItem WHERE Uid=@Uid AND Year=@Year AND Month=@Month AND Type=@Type;", firstItem);
        if (existCount == total && existCount > 0)
        {
            // 总数未变，仅刷新最新一条记录（复用探针请求结果，无需额外网络请求）
            var lastItem = firstPage.List.FirstOrDefault();
            if (lastItem != null)
            {
                using var t = dapper.BeginTransaction();
                dapper.Execute("""
                    DELETE FROM GenshinTravelersDiaryAwardItem
                    WHERE Id = (
                        SELECT Id FROM GenshinTravelersDiaryAwardItem
                        WHERE Uid = @Uid AND Year = @Year AND Month = @Month AND Type = @Type
                        ORDER BY Time DESC LIMIT 1
                    );
                    """, firstItem, t);
                dapper.Execute("""
                    INSERT INTO GenshinTravelersDiaryAwardItem (Uid, Year, Month, Type, ActionId, ActionName, Time, Number)
                    VALUES (@Uid, @Year, @Month, @Type, @ActionId, @ActionName, @Time, @Number);
                    """, lastItem, t);
                t.Commit();
            }
            return 0;
        }
        if (existCount >= total)
        {
            return 0;
        }
        // 增量插入：仅在有新数据时才发起全量请求
        var detail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTravelsDiaryDetailAsync(role, month, type, limit));
        var list = detail.List;
        var existTimes = new HashSet<DateTime>(dapper.Query<DateTime>(
            "SELECT Time FROM GenshinTravelersDiaryAwardItem WHERE Uid=@Uid AND Year=@Year AND Month=@Month AND Type=@Type;",
            firstItem));
        var newItems = list.Where(x => !existTimes.Contains(x.Time)).ToList();
        if (newItems.Count > 0)
        {
            dapper.Execute("""
                INSERT INTO GenshinTravelersDiaryAwardItem (Uid, Year, Month, Type, ActionId, ActionName, Time, Number)
                VALUES (@Uid, @Year, @Month, @Type, @ActionId, @ActionName, @Time, @Number);
                """, newItems);
        }
        // 刷新原有记录中最新的一条（API 按时间降序，新记录之后的第一条即为原最新记录）
        int newCount = newItems.Count;
        if (newCount < list.Count)
        {
            var lastExistingItem = list[newCount];
            using var updateTx = dapper.BeginTransaction();
            dapper.Execute("""
                DELETE FROM GenshinTravelersDiaryAwardItem
                WHERE Id = (
                    SELECT Id FROM GenshinTravelersDiaryAwardItem
                    WHERE Uid = @Uid AND Year = @Year AND Month = @Month AND Type = @Type AND Time = @Time
                    LIMIT 1
                );
                """, lastExistingItem, updateTx);
            dapper.Execute("""
                INSERT INTO GenshinTravelersDiaryAwardItem (Uid, Year, Month, Type, ActionId, ActionName, Time, Number)
                VALUES (@Uid, @Year, @Month, @Type, @ActionId, @ActionName, @Time, @Number);
                """, lastExistingItem, updateTx);
            updateTx.Commit();
        }
        return newCount;
    }



    public List<TravelersDiaryAwardItem> GetTravelersDiaryDetailItems(long uid, int year, int month, int type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TravelersDiaryAwardItem>("SELECT * FROM GenshinTravelersDiaryAwardItem WHERE Uid=@uid AND Year=@year AND Month=@month AND Type=@type ORDER BY Time;", new { uid, year, month, type }).ToList();
    }


    /// <summary>
    /// 一次查询旅行札记某月所有类型的明细记录，由调用方按 <see cref="TravelersDiaryAwardItem.Type"/> 分组汇总。
    /// </summary>
    /// <param name="uid">玩家 uid。</param>
    /// <param name="year">年份。</param>
    /// <param name="month">月份（1-12）。</param>
    /// <returns>该月全部类型的明细记录，按时间升序排列。</returns>
    public List<TravelersDiaryAwardItem> GetTravelersDiaryDetailItems(long uid, int year, int month)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TravelersDiaryAwardItem>("SELECT * FROM GenshinTravelersDiaryAwardItem WHERE Uid=@uid AND Year=@year AND Month=@month ORDER BY Time;", new { uid, year, month }).ToList();
    }





    #endregion




    #region Imaginarium Theater



    /// <summary>
    /// 幻想真境剧诗
    /// </summary>
    /// <param name="role"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RefreshImaginariumTheaterInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var infos = await ExecuteWithRequestRecoveryAsync(role, client => client.GetImaginariumTheaterInfosAsync(role, cancellationToken), cancellationToken);
        if (infos.Count == 0)
        {
            return;
        }
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        foreach (var info in infos)
        {
            // 米游社当期详情常延迟，可能只有 Stat；不要用空 Detail 覆盖已缓存的幕次阵容
            PreserveCachedImaginariumTheaterDetails(role, info);
            var obj = new
            {
                info.Uid,
                info.ScheduleId,
                info.StartTime,
                info.EndTime,
                info.DifficultyId,
                info.MaxRoundId,
                info.Heraldry,
                info.MedalNum,
                Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
            INSERT OR REPLACE INTO GenshinImaginariumTheaterInfo (Uid, ScheduleId, StartTime, EndTime, DifficultyId, MaxRoundId, Heraldry, MedalNum, Value)
            VALUES (@Uid, @ScheduleId, @StartTime, @EndTime, @DifficultyId, @MaxRoundId, @Heraldry, @MedalNum, @Value);
            """, obj, t);
        }
        t.Commit();
    }




    public List<ImaginariumTheaterInfo> GetImaginariumTheaterInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ImaginariumTheaterInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ImaginariumTheaterInfo>("""
            SELECT Uid, ScheduleId, StartTime, EndTime, DifficultyId, MaxRoundId, Heraldry, MedalNum FROM GenshinImaginariumTheaterInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ImaginariumTheaterInfo? GetImaginariumTheaterInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinImaginariumTheaterInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ImaginariumTheaterInfo>(value);
    }


    /// <summary>
    /// 当期响应缺少演出详情时，保留同期已缓存的 Detail，只更新 Stat 等概要字段。
    /// </summary>
    private void PreserveCachedImaginariumTheaterDetails(GameRecordRole role, ImaginariumTheaterInfo info)
    {
        if (info.HasDetailContent)
        {
            return;
        }
        if (GetImaginariumTheaterInfo(role, info.ScheduleId) is not ImaginariumTheaterInfo existing)
        {
            return;
        }
        if (existing.HasDetailContent)
        {
            info.Detail = existing.Detail;
            info.HasDetailData = true;
        }
    }



    #endregion




    #region Simulated Universe



    public async Task<SimulatedUniverseInfo> GetSimulatedUniverseInfoAsync(GameRecordRole role, bool detail)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetSimulatedUniverseInfoAsync(role, detail));
        if (detail)
        {
            using var dapper = DatabaseService.CreateConnection();
            using var t = dapper.BeginTransaction();
            var obj = new
            {
                role.Uid,
                info.LastRecord.Basic.ScheduleId,
                info.LastRecord.Basic.FinishCount,
                info.LastRecord.Basic.ScheduleBegin,
                info.LastRecord.Basic.ScheduleEnd,
                info.LastRecord.HasData,
                Value = JsonSerializer.Serialize(info.LastRecord, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailSimulatedUniverseRecord (Uid, ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd, HasData, Value)
                VALUES (@Uid, @ScheduleId, @FinishCount, @ScheduleBegin, @ScheduleEnd, @HasData, @Value);
                """, obj, t);
            obj = new
            {
                role.Uid,
                info.CurrentRecord.Basic.ScheduleId,
                info.CurrentRecord.Basic.FinishCount,
                info.CurrentRecord.Basic.ScheduleBegin,
                info.CurrentRecord.Basic.ScheduleEnd,
                info.CurrentRecord.HasData,
                Value = JsonSerializer.Serialize(info.CurrentRecord, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailSimulatedUniverseRecord (Uid, ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd, HasData, Value)
                VALUES (@Uid, @ScheduleId, @FinishCount, @ScheduleBegin, @ScheduleEnd, @HasData, @Value);
                """, obj, t);
            t.Commit();
        }
        return info;
    }



    public List<SimulatedUniverseRecordBasic> GetSimulatedUniverseRecordBasics(GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<SimulatedUniverseRecordBasic>("""
            SELECT ScheduleId, FinishCount, ScheduleBegin, ScheduleEnd FROM StarRailSimulatedUniverseRecord WHERE Uid=@Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid }).ToList();
    }



    public SimulatedUniverseRecord? GetSimulatedUniverseRecord(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailSimulatedUniverseRecord WHERE Uid=@Uid AND ScheduleId=@scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<SimulatedUniverseRecord>(value);
    }



    #endregion




    #region Forgotten Hall



    public async Task<ForgottenHallInfo> RefreshForgottenHallInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetForgottenHallInfoAsync(role, schedule), cancellationToken);
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailForgottenHallInfo (Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<ForgottenHallInfo> GetForgottenHallInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ForgottenHallInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ForgottenHallInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailForgottenHallInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ForgottenHallInfo? GetForgottenHallInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailForgottenHallInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ForgottenHallInfo>(value);
    }



    #endregion




    #region Pure Fiction



    public async Task<PureFictionInfo> RefreshPureFictionInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetPureFictionInfoAsync(role, schedule), cancellationToken);
        if (info.ScheduleId == 0)
        {
            return info;
        }
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailPureFictionInfo (Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<PureFictionInfo> GetPureFictionInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<PureFictionInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<PureFictionInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailPureFictionInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public PureFictionInfo? GetPureFictionInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailPureFictionInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<PureFictionInfo>(value);
    }



    #endregion




    #region Apocalyptic Shadow



    public async Task<ApocalypticShadowInfo> RefreshApocalypticShadowInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetApocalypticShadowInfoAsync(role, schedule), cancellationToken);
        if (info.ScheduleId == 0)
        {
            return info;
        }
        var obj = new
        {
            info.Uid,
            info.ScheduleId,
            info.BeginTime,
            info.EndTime,
            info.UpperBossIcon,
            info.LowerBossIcon,
            info.TierceBossIcon,
            info.StarNum,
            info.ExtraStarNum,
            info.MaxFloor,
            info.BattleNum,
            info.HasData,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailApocalypticShadowInfo (Uid, ScheduleId, BeginTime, EndTime, UpperBossIcon, LowerBossIcon, TierceBossIcon, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData, Value)
            VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @UpperBossIcon, @LowerBossIcon, @TierceBossIcon, @StarNum, @ExtraStarNum, @MaxFloor, @BattleNum, @HasData, @Value);
            """, obj);
        return info;
    }



    public List<ApocalypticShadowInfo> GetApocalypticShadowInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ApocalypticShadowInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ApocalypticShadowInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, UpperBossIcon, LowerBossIcon, TierceBossIcon, StarNum, ExtraStarNum, MaxFloor, BattleNum, HasData FROM StarRailApocalypticShadowInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ApocalypticShadowInfo? GetApocalypticShadowInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM StarRailApocalypticShadowInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ApocalypticShadowInfo>(value);
    }



    #endregion




    #region Trailblaze Calendar



    public async Task<TrailblazeCalendarSummary> GetTrailblazeCalendarSummaryAsync(GameRecordRole role, string month = "")
    {
        var summary = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTrailblazeCalendarSummaryAsync(role, month));
        if (summary.MonthData is null)
        {
            return summary;
        }
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO StarRailTrailblazeCalendarMonthData (Uid, Month, CurrentHcoin, CurrentRailsPass, LastHcoin, LastRailsPass, HcoinRate, RailsRate, GroupBy)
            VALUES (@Uid, @Month, @CurrentHcoin, @CurrentRailsPass, @LastHcoin, @LastRailsPass, @HcoinRate, @RailsRate, @GroupBy);
            """, summary.MonthData);
        return summary;
    }


    public List<TrailblazeCalendarMonthData> GetTrailblazeCalendarMonthDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<TrailblazeCalendarMonthData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<TrailblazeCalendarMonthData>("SELECT * FROM StarRailTrailblazeCalendarMonthData WHERE Uid = @Uid ORDER BY Month DESC;", new { role.Uid });
        return list.ToList();
    }


    //原数据库使用的是自增id，在做增量更新时，逻辑判断比较复杂
    /// <param name="forceOverwrite">为 true 时先删除该 (uid, month, type) 的全部旧记录，再全量写入 API 返回值；false 时走增量逻辑。</param>
    public async Task<int> GetTrailblazeCalendarDetailAsync(GameRecordRole role, string month, int type, bool forceOverwrite = false)
    {
        if (forceOverwrite)
        {
            // 全量覆盖：先删除旧数据，再批量写入 API 全部记录
            var fwDetail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTrailblazeCalendarDetailAsync(role, month, type));
            var fwList = fwDetail.List;
            if (fwList.Count == 0)
            {
                return 0;
            }
            using var fwDapper = DatabaseService.CreateConnection();
            using var fwTx = fwDapper.BeginTransaction();
            fwDapper.Execute("""
                DELETE FROM StarRailTrailblazeCalendarDetailItem
                WHERE Uid = @Uid AND Month = @Month AND Type = @Type;
                """, new { Uid = role.Uid, Month = month, Type = type }, fwTx);
            fwDapper.Execute("""
                INSERT INTO StarRailTrailblazeCalendarDetailItem (Uid, Month, Type, Action, ActionName, Time, Number)
                VALUES (@Uid, @Month, @Type, @Action, @ActionName, @Time, @Number);
                """, fwList, fwTx);
            fwTx.Commit();
            return fwList.Count;
        }

        // 先获取第一页（page_size=1）以同时得到总数和最新一条记录
        var firstPage = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTrailblazeCalendarDetailByPageAsync(role, month, type, 1, 1));
        int total = firstPage.Total;
        if (total == 0)
        {
            return 0;
        }
        using var dapper = DatabaseService.CreateConnection();
        // Microsoft.Data.Sqlite 区分参数名大小写；必须与 SQL 占位符一致（@Uid/@Month/@Type）
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM StarRailTrailblazeCalendarDetailItem WHERE Uid = @Uid AND Month = @Month AND Type = @Type;", new { Uid = role.Uid, Month = month, Type = type });
        if (existCount == total && existCount > 0)
        {
            // 总数未变，仅刷新最新一条记录
            var lastItem = firstPage.List.FirstOrDefault();
            if (lastItem != null)
            {
                using var t = dapper.BeginTransaction();
                dapper.Execute("""
                    DELETE FROM StarRailTrailblazeCalendarDetailItem
                    WHERE Id = (
                        SELECT Id FROM StarRailTrailblazeCalendarDetailItem
                        WHERE Uid = @Uid AND Month = @Month AND Type = @Type
                        ORDER BY Time DESC LIMIT 1
                    );
                    """, lastItem, t);
                dapper.Execute("""
                    INSERT INTO StarRailTrailblazeCalendarDetailItem (Uid, Month, Type, Action, ActionName, Time, Number)
                    VALUES (@Uid, @Month, @Type, @Action, @ActionName, @Time, @Number);
                    """, lastItem, t);
                t.Commit();
            }
            return 0;
        }
        if (existCount >= total)
        {
            return 0;
        }
        // 增量插入：仅插入 Time 不重复的新记录；同时刷新原有记录中最新的一条
        var detail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetTrailblazeCalendarDetailAsync(role, month, type));
        var list = detail.List;
        var existTimes = new HashSet<DateTime>(dapper.Query<DateTime>(
            "SELECT Time FROM StarRailTrailblazeCalendarDetailItem WHERE Uid = @Uid AND Month = @Month AND Type = @Type;",
            new { Uid = role.Uid, Month = month, Type = type }));
        var newItems = list.Where(x => !existTimes.Contains(x.Time)).ToList();
        if (newItems.Count > 0)
        {
            dapper.Execute("""
                INSERT INTO StarRailTrailblazeCalendarDetailItem (Uid, Month, Type, Action, ActionName, Time, Number)
                VALUES (@Uid, @Month, @Type, @Action, @ActionName, @Time, @Number);
                """, newItems);
        }
        // 刷新原有记录中最新的一条（API 按时间降序，新记录之后的第一条即为原最新记录）
        int newCount = newItems.Count;
        if (newCount < list.Count)
        {
            var lastExistingItem = list[newCount];
            using var updateTx = dapper.BeginTransaction();
            dapper.Execute("""
                DELETE FROM StarRailTrailblazeCalendarDetailItem
                WHERE Id = (
                    SELECT Id FROM StarRailTrailblazeCalendarDetailItem
                    WHERE Uid = @Uid AND Month = @Month AND Type = @Type AND Time = @Time
                    LIMIT 1
                );
                """, lastExistingItem, updateTx);
            dapper.Execute("""
                INSERT INTO StarRailTrailblazeCalendarDetailItem (Uid, Month, Type, Action, ActionName, Time, Number)
                VALUES (@Uid, @Month, @Type, @Action, @ActionName, @Time, @Number);
                """, lastExistingItem, updateTx);
            updateTx.Commit();
        }
        return newCount;
    }



    public List<TrailblazeCalendarDetailItem> GetTrailblazeCalendarDetailItems(long uid, string month, int type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TrailblazeCalendarDetailItem>("SELECT * FROM StarRailTrailblazeCalendarDetailItem WHERE Uid=@Uid AND Month=@Month AND Type=@Type ORDER BY Time;", new { Uid = uid, Month = month, Type = type }).ToList();
    }


    /// <summary>
    /// 一次查询开拓月历某月所有类型的明细记录，由调用方按 <see cref="TrailblazeCalendarDetailItem.Type"/> 分组汇总。
    /// </summary>
    /// <param name="uid">玩家 uid。</param>
    /// <param name="month">月份字符串，格式 yyyyMM（如 202506）。</param>
    /// <returns>该月全部类型的明细记录，按时间升序排列。</returns>
    public List<TrailblazeCalendarDetailItem> GetTrailblazeCalendarDetailItems(long uid, string month)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<TrailblazeCalendarDetailItem>("SELECT * FROM StarRailTrailblazeCalendarDetailItem WHERE Uid=@Uid AND Month=@Month ORDER BY Time;", new { Uid = uid, Month = month }).ToList();
    }




    #endregion




    #region Inter Knot Report



    public async Task<InterKnotReportSummary> GetInterKnotReportSummaryAsync(GameRecordRole role, string month = "")
    {
        var summary = await ExecuteWithRequestRecoveryAsync(role, client => client.GetInterKnotReportSummaryAsync(role, month));
        using var dapper = DatabaseService.CreateConnection();
        var obj = new
        {
            summary.Uid,
            summary.DataMonth,
            Value = JsonSerializer.Serialize(summary),
        };
        dapper.Execute("""
            INSERT OR REPLACE INTO ZZZInterKnotReportSummary (Uid, DataMonth, Value) VALUES (@Uid, @DataMonth, @Value);
            """, obj);
        return summary;
    }


    public List<InterKnotReportSummary> GetInterKnotReportSummaryList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<InterKnotReportSummary>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var values = dapper.Query<string>("SELECT Value FROM ZZZInterKnotReportSummary WHERE Uid = @Uid ORDER BY DataMonth DESC;", new { role.Uid });
        return values.Select(v => JsonSerializer.Deserialize<InterKnotReportSummary>(v)!).ToList();
    }


    public InterKnotReportSummary? GetInterKnotReportSummary(InterKnotReportSummary summary)
    {
        if (summary is null)
        {
            return null;
        }
        using var dapper = DatabaseService.CreateConnection();
        string? value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM ZZZInterKnotReportSummary WHERE Uid = @Uid AND DataMonth = @DataMonth LIMIT 1;", summary);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<InterKnotReportSummary>(value);
    }



    /// <param name="forceOverwrite">为 true 时先删除该 (uid, month, type) 的全部旧记录，再全量写入 API 返回值；false 时走增量逻辑。</param>
    public async Task<int> GetInterKnotReportDetailAsync(GameRecordRole role, string month, string type, bool forceOverwrite = false)
    {
        if (forceOverwrite)
        {
            // 全量覆盖：先删除旧数据，再批量写入 API 全部记录
            var fwDetail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetInterKnotReportDetailAsync(role, month, type));
            var fwList = fwDetail.List;
            if (fwList.Count == 0)
            {
                return 0;
            }
            using var fwDapper = DatabaseService.CreateConnection();
            using var fwTx = fwDapper.BeginTransaction();
            fwDapper.Execute("""
                DELETE FROM ZZZInterKnotReportDetailItem
                WHERE Uid = @Uid AND DataMonth = @DataMonth AND DataType = @DataType;
                """, new { role.Uid, DataMonth = month, DataType = type }, fwTx);
            fwDapper.Execute("""
                INSERT OR REPLACE INTO ZZZInterKnotReportDetailItem (Uid, Id, DataMonth, DataType, Action, Time, Number)
                VALUES (@Uid, @Id, @DataMonth, @DataType, @Action, @Time, @Number);
                """, fwList, fwTx);
            fwTx.Commit();
            return fwList.Count;
        }

        // 先获取第一页（page_size=1）以同时得到总数和最新一条记录，避免后续重复请求
        var firstPage = await ExecuteWithRequestRecoveryAsync(role, client => client.GetInterKnotReportDetailByPageAsync(role, month, type, 1, 1));
        int total = firstPage.Total;
        if (total == 0)
        {
            return 0;
        }
        using var dapper = DatabaseService.CreateConnection();
        var existCount = dapper.QuerySingleOrDefault<int>("SELECT COUNT(*) FROM ZZZInterKnotReportDetailItem WHERE Uid = @Uid AND DataMonth = @month AND DataType = @type;", new { role.Uid, month, type });
        if (existCount == total && existCount > 0)
        {
            // 总数未变，仅刷新最新一条记录（复用首页请求结果，无需额外网络请求）
            var lastItem = firstPage.List.FirstOrDefault();
            if (lastItem != null)
            {
                dapper.Execute("""
                    INSERT OR REPLACE INTO ZZZInterKnotReportDetailItem (Uid, Id, DataMonth, DataType, Action, Time, Number)
                    VALUES (@Uid, @Id, @DataMonth, @DataType, @Action, @Time, @Number);
                    """, lastItem);
            }
            return 0;
        }
        if (existCount >= total)
        {
            return 0;
        }
        // 增量插入：INSERT OR IGNORE 跳过已存在记录（主键为 (Uid, Id)），仅写入新记录；
        // 同时刷新原有记录中最新的一条（INSERT OR REPLACE 利用主键做 upsert）
        var detail = await ExecuteWithRequestRecoveryAsync(role, client => client.GetInterKnotReportDetailAsync(role, month, type));
        int newCount = total - existCount;
        dapper.Execute("""
            INSERT OR IGNORE INTO ZZZInterKnotReportDetailItem (Uid, Id, DataMonth, DataType, Action, Time, Number)
            VALUES (@Uid, @Id, @DataMonth, @DataType, @Action, @Time, @Number);
            """, detail.List);
        if (newCount < detail.List.Count)
        {
            dapper.Execute("""
                INSERT OR REPLACE INTO ZZZInterKnotReportDetailItem (Uid, Id, DataMonth, DataType, Action, Time, Number)
                VALUES (@Uid, @Id, @DataMonth, @DataType, @Action, @Time, @Number);
                """, detail.List[newCount]);
        }
        return newCount;
    }



    public List<InterKnotReportDetailItem> GetInterKnotReportDetailItems(long uid, string month, string type)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<InterKnotReportDetailItem>("SELECT * FROM ZZZInterKnotReportDetailItem WHERE Uid=@uid AND DataMonth=@month AND DataType=@type ORDER BY Time;", new { uid, month, type }).ToList();
    }


    /// <summary>
    /// 一次查询绳网月报某月所有类型的明细记录，由调用方按 <see cref="InterKnotReportDetailItem.DataType"/> 分组汇总。
    /// </summary>
    /// <param name="uid">玩家 uid。</param>
    /// <param name="month">月份字符串，格式 yyyyMM（如 202506）。</param>
    /// <returns>该月全部类型的明细记录，按时间升序排列。</returns>
    public List<InterKnotReportDetailItem> GetInterKnotReportDetailItems(long uid, string month)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.Query<InterKnotReportDetailItem>("SELECT * FROM ZZZInterKnotReportDetailItem WHERE Uid=@uid AND DataMonth=@month ORDER BY Time;", new { uid, month }).ToList();
    }


    public async Task<ZZZGachaRecordData> GetZZZGachaRecordAsync(GameRecordRole role, int gachaType, long? endId = null, string? language = null, CancellationToken cancellationToken = default)
    {
        if (role is null)
        {
            throw new ArgumentNullException(nameof(role));
        }
        bool isHoyolab = IsGlobalServerRole(role);
        if (isHoyolab && !string.IsNullOrWhiteSpace(language))
        {
            // HoYoLAB 语言由请求头决定，统一通过 HoyolabClient.Language 生效。
            Language = language;
        }
        if (!isHoyolab)
        {
            await UpdateHyperionDeviceFpWithLockAsync(false, cancellationToken);
        }
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GetZZZGachaRecordAsync(role, gachaType, endId, language, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 通过养成指南 icon_info + item_list 获取绝区零抽卡物品元数据（名称、图标、稀有度等）。
    /// 任意已登录绝区零角色即可；国际服语言由 <paramref name="language"/> 控制。
    /// </summary>
    /// <param name="role">已登录的绝区零角色。</param>
    /// <param name="language">期望语言；国服通常仅中文名称，国际服可切换。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并后的 wiki（含 Language 与 List）。</returns>
    public async Task<ZZZGachaWiki> GetZZZGachaWikiFromCultivateToolAsync(GameRecordRole role, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        bool isHoyolab = IsGlobalServerRole(role);
        string lang = LanguageUtil.FilterLanguage(language);
        if (isHoyolab)
        {
            Language = lang;
        }
        else
        {
            await UpdateHyperionDeviceFpWithLockAsync(false, cancellationToken);
        }
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GetZZZGachaWikiAsync(role, lang, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 通过米游社 stoken 换取抽卡 authkey（仅国服；国际服不支持）。
    /// 用于原神/星铁「从米游社同步」：拿到 authkey 后拼 public-operation URL，再走既有 GachaLogClient。
    /// </summary>
    /// <param name="role">含 stoken+mid Cookie 的国服角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>authkey 结果。</returns>
    public async Task<GameAuthKey> GenAuthKeyAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (IsGlobalServerRole(role))
        {
            throw new NotSupportedException("Generating gacha authkey from HoYoLAB SToken is not supported.");
        }
        await UpdateHyperionDeviceFpWithLockAsync(false, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GenAuthKeyAsync(role, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 将 genAuthKey 结果拼成可被 <see cref="Starward.Core.Gacha.GachaLogClient"/> 解析的 public-operation 抽卡 URL。
    /// 当前仅用于原神国服/B 服的「从米游社同步」；星铁未开放该入口。
    /// </summary>
    /// <param name="launcherGameBiz">当前启动器选中的游戏（含 bilibili 服；决定 API 主机）。</param>
    /// <param name="role">米游社角色，提供绑定接口的 <c>game_biz</c> 与 <c>region</c>。</param>
    /// <param name="authKey">genAuthKey 返回值。</param>
    /// <param name="lang">物品名称语言，如 zh-cn；为空时默认 zh-cn。</param>
    /// <returns>含 authkey 等查询参数的完整 API URL（带 ?，后续可再接 &amp;gacha_type=）。</returns>
    /// <exception cref="NotSupportedException">非原神国服（含 B 服）时抛出。</exception>
    public static string BuildGachaLogUrlFromAuthKey(GameBiz launcherGameBiz, GameRecordRole role, GameAuthKey authKey, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(authKey);
        if (string.IsNullOrWhiteSpace(authKey.Authkey))
        {
            throw new ArgumentException("Authkey is empty.", nameof(authKey));
        }
        if (launcherGameBiz.Game != GameBiz.hk4e || !(launcherGameBiz.IsChinaServer() || launcherGameBiz.IsBilibili()))
        {
            throw new NotSupportedException($"Building gacha URL from authkey is only supported for Genshin Impact CN servers (current: {launcherGameBiz}).");
        }

        const string apiPrefix = "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getGachaLog";
        // 查询参数中的 game_biz 用绑定角色的 biz（hk4e_cn），不要用 bilibili 启动器 biz
        string roleGameBiz = string.IsNullOrWhiteSpace(role.GameBiz) ? GameBiz.hk4e_cn : role.GameBiz;
        string language = string.IsNullOrWhiteSpace(lang) ? "zh-cn" : LanguageUtil.FilterLanguage(lang);
        int authkeyVer = authKey.AuthkeyVer > 0 ? authKey.AuthkeyVer : 1;
        int signType = authKey.SignType > 0 ? authKey.SignType : 2;

        // 对齐 TeyvatGuide / Snap.Hutao：authkey 四件套 + game_biz（及可选 region）
        var sb = new System.Text.StringBuilder(apiPrefix);
        sb.Append("?auth_appid=webview_gacha");
        sb.Append("&authkey=").Append(Uri.EscapeDataString(authKey.Authkey));
        sb.Append("&authkey_ver=").Append(authkeyVer);
        sb.Append("&sign_type=").Append(signType);
        sb.Append("&lang=").Append(Uri.EscapeDataString(language));
        sb.Append("&game_biz=").Append(Uri.EscapeDataString(roleGameBiz));
        if (!string.IsNullOrWhiteSpace(role.Region))
        {
            sb.Append("&region=").Append(Uri.EscapeDataString(role.Region));
        }
        return sb.ToString();
    }




    #endregion




    #region Shiyu Defense



    public async Task<ShiyuDefenseWrapper> RefreshShiyuDefenseInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var wrapper = await ExecuteWithRequestRecoveryAsync(role, client => client.GetShiyuDefenseInfoAsync(role, schedule), cancellationToken);
        if (wrapper.HadalVer is "v1" && wrapper.InfoV1 is not null)
        {
            var info = wrapper.InfoV1;
            if (info.HasData)
            {
                var obj = new
                {
                    role.Uid,
                    info.ScheduleId,
                    info.BeginTime,
                    info.EndTime,
                    info.Version,
                    info.HasData,
                    info.MaxRating,
                    info.MaxRatingTimes,
                    info.MaxLayer,
                    Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
                };
                using var dapper = DatabaseService.CreateConnection();
                dapper.Execute("""
                    INSERT OR REPLACE INTO ZZZShiyuDefenseInfo (Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, MaxRatingTimes, MaxLayer, Value)
                    VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @Version, @HasData, @MaxRating, @MaxRatingTimes, @MaxLayer, @Value);
                    """, obj);
            }
        }
        else if (wrapper.HadalVer is "v2" && wrapper.InfoV2 is not null)
        {
            if (wrapper.InfoV2.Brief is not null)
            {
                var info = wrapper.InfoV2;
                if (info.PassFifthFloor)
                {
                    // 米游社当期详情常延迟，可能只有总分/排名/评价；不要用空防线覆盖已缓存的阵容
                    PreserveCachedShiyuDefenseV2LayerDetails(role, info);
                    var obj = new
                    {
                        role.Uid,
                        info.ScheduleId,
                        info.BeginTime,
                        info.EndTime,
                        info.Version,
                        info.HasData,
                        info.MaxRating,
                        info.V2Score,
                        Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
                    };
                    using var dapper = DatabaseService.CreateConnection();
                    dapper.Execute("""
                        INSERT OR REPLACE INTO ZZZShiyuDefenseInfo (Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, V2Score, Value)
                        VALUES (@Uid, @ScheduleId, @BeginTime, @EndTime, @Version, @HasData, @MaxRating, @V2Score, @Value);
                        """, obj);
                }
            }
        }
        return wrapper;
    }


    /// <summary>
    /// 当期响应缺少防线详情时，保留同期已缓存的阵容，只更新 Brief 等概要字段。
    /// </summary>
    private void PreserveCachedShiyuDefenseV2LayerDetails(GameRecordRole role, ShiyuDefenseInfoV2 info)
    {
        if (info.HasLayerDetails)
        {
            return;
        }
        if (GetShiyuDefenseInfo(role, info.ScheduleId) is not ShiyuDefenseInfoV2 existing)
        {
            return;
        }
        if (existing.HasFifthLayerChallenges)
        {
            info.FifthLayerDetail = existing.FifthLayerDetail;
        }
        if (existing.HasFourthLayerDetail)
        {
            info.FourthLayerDetail = existing.FourthLayerDetail;
        }
    }



    public List<ShiyuDefenseInfo> GetShiyuDefenseInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ShiyuDefenseInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ShiyuDefenseInfo>("""
            SELECT Uid, ScheduleId, BeginTime, EndTime, Version, HasData, MaxRating, MaxRatingTimes, MaxLayer, V2Score FROM ZZZShiyuDefenseInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public ShiyuDefenseInfoBase? GetShiyuDefenseInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        (string version, string value) = dapper.QueryFirstOrDefault<(string Version, string Value)>("""
            SELECT Version, Value FROM ZZZShiyuDefenseInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (version is "v1")
        {
            return JsonSerializer.Deserialize<ShiyuDefenseInfo>(value);
        }
        else if (version is "v2")
        {
            return JsonSerializer.Deserialize<ShiyuDefenseInfoV2>(value);
        }
        return null;
    }



    #endregion




    #region Deadly Assault



    public async Task<DeadlyAssaultInfo> RefreshDeadlyAssaultInfoAsync(GameRecordRole role, int schedule, CancellationToken cancellationToken = default)
    {
        var info = await ExecuteWithRequestRecoveryAsync(role, client => client.GetDeadlyAssaultInfoAsync(role, schedule), cancellationToken);
        if (!info.HasData)
        {
            return info;
        }
        // 米游社当期节点详情常延迟，可能只有总分/排名；不要用空节点覆盖已缓存的阵容
        PreserveCachedDeadlyAssaultNodeDetails(role, info);
        var obj = new
        {
            role.Uid,
            info.ZoneId,
            info.StartTime,
            info.EndTime,
            info.HasData,
            info.RankPercent,
            info.TotalScore,
            info.TotalStar,
            Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
        };
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("""
            INSERT OR REPLACE INTO ZZZDeadlyAssaultInfo (Uid, ZoneId, StartTime, EndTime, HasData, RankPercent, TotalScore, TotalStar, Value)
            VALUES (@Uid, @ZoneId, @StartTime, @EndTime, @HasData, @RankPercent, @TotalScore, @TotalStar, @Value);
            """, obj);
        return info;
    }



    public List<DeadlyAssaultInfo> GetDeadlyAssaultInfoList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<DeadlyAssaultInfo>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<DeadlyAssaultInfo>("""
            SELECT Uid, ZoneId, StartTime, EndTime, HasData, RankPercent, TotalScore, TotalStar FROM ZZZDeadlyAssaultInfo WHERE Uid = @Uid ORDER BY ZoneId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public DeadlyAssaultInfo? GetDeadlyAssaultInfo(GameRecordRole role, int zoneId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM ZZZDeadlyAssaultInfo WHERE Uid = @Uid And ZoneId = @zoneId LIMIT 1;
            """, new { role.Uid, zoneId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<DeadlyAssaultInfo>(value);
    }


    /// <summary>
    /// 当期响应缺少节点详情时，保留同期已缓存的常规/绝境阵容，只更新总分等概要字段。
    /// </summary>
    private void PreserveCachedDeadlyAssaultNodeDetails(GameRecordRole role, DeadlyAssaultInfo info)
    {
        if (info.HasNodeDetails)
        {
            return;
        }
        if (GetDeadlyAssaultInfo(role, info.ZoneId) is not DeadlyAssaultInfo existing)
        {
            return;
        }
        if (existing.HasNormalNodes)
        {
            info.AllNodes = existing.AllNodes;
        }
        if (existing.HasHardNodes)
        {
            info.HardList = existing.HardList;
            info.HasHard = true;
        }
    }



    #endregion




    #region Daily Note



    public async Task<BH3DailyNote> GetBH3DailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(BH3DailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out BH3DailyNote? note))
        {
            note = await ExecuteWithRequestRecoveryAsync(role, client => client.GetBH3DailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }



    public async Task<GenshinDailyNote> GetGenshinDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(GenshinDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out GenshinDailyNote? note))
        {
            note = await ExecuteWithRequestRecoveryAsync(role, client => client.GetGenshinDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }



    public async Task<StarRailDailyNote> GetStarRailDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(StarRailDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out StarRailDailyNote? note))
        {
            note = await ExecuteWithRequestRecoveryAsync(role, client => client.GetStarRailDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }


    public async Task<ZZZDailyNote> GetZZZDailyNoteAsync(GameRecordRole role, bool forceUpdate = false, CancellationToken cancellationToken = default)
    {
        string key = $"{nameof(ZZZDailyNote)}_{role.Region}_{role.Uid}";
        if (forceUpdate || !_memoryCache.TryGetValue(key, out ZZZDailyNote? note))
        {
            note = await ExecuteWithRequestRecoveryAsync(role, client => client.GetZZZDailyNoteAsync(role, cancellationToken), cancellationToken);
            _memoryCache.Set(key, note, TimeSpan.FromMinutes(5));
        }
        return note!;
    }




    #endregion




    #region Stygian Onslaught


    public async Task<List<StygianOnslaughtInfo>> RefreshStygianOnslaughtInfosAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        var infos = await ExecuteWithRequestRecoveryAsync(role, client => client.GetStygianOnslaughtInfosAsync(role, cancellationToken), cancellationToken);
        if (infos.Count == 0)
        {
            return infos;
        }
        using var dapper = DatabaseService.CreateConnection();
        using var t = dapper.BeginTransaction();
        foreach (var info in infos)
        {
            var obj = new
            {
                info.Uid,
                info.ScheduleId,
                info.StartDateTime,
                info.EndDateTime,
                info.Difficulty,
                info.Second,
                Value = JsonSerializer.Serialize(info, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
            INSERT OR REPLACE INTO GenshinStygianOnslaughtInfo (Uid, ScheduleId, StartDateTime, EndDateTime, Difficulty, Second, Value)
            VALUES (@Uid, @ScheduleId, @StartDateTime, @EndDateTime, @Difficulty, @Second, @Value);
            """, obj, t);
        }
        t.Commit();
        return infos;
    }



    public List<StygianOnslaughtInfo> GetStygianOnslaughtInfoList(GameRecordRole role)
    {
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<StygianOnslaughtInfo>("""
            SELECT Uid, ScheduleId, StartDateTime, EndDateTime, Difficulty, Second FROM GenshinStygianOnslaughtInfo WHERE Uid = @Uid ORDER BY ScheduleId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public StygianOnslaughtInfo? GetStygianOnslaughtInfo(GameRecordRole role, int scheduleId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var value = dapper.QueryFirstOrDefault<string>("""
            SELECT Value FROM GenshinStygianOnslaughtInfo WHERE Uid = @Uid And ScheduleId = @scheduleId LIMIT 1;
            """, new { role.Uid, scheduleId });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonSerializer.Deserialize<StygianOnslaughtInfo>(value);
    }



    #endregion




    #region Star Rail Challenge Peak




    public List<ChallengePeakData> GetStarRailChallengePeakDataList(GameRecordRole role)
    {
        if (role is null)
        {
            return new List<ChallengePeakData>();
        }
        using var dapper = DatabaseService.CreateConnection();
        var list = dapper.Query<ChallengePeakData>("""
            SELECT Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon FROM StarRailChallengePeakData WHERE Uid = @Uid ORDER BY GroupId DESC;
            """, new { role.Uid });
        return list.ToList();
    }



    public async Task RefreshStarRailChallengePeakDataAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        using var dapper = DatabaseService.CreateConnection();

        var data = await ExecuteWithRequestRecoveryAsync(role, client => client.GetStarRailChallengePeakDataAsync(role, 1, cancellationToken), cancellationToken);
        if (data.ChallengePeakRecords?.Count == 1)
        {
            var record = data.ChallengePeakRecords[0];
            var obj = new
            {
                role.Uid,
                record.Group.GroupId,
                record.Group.GameVersion,
                record.BossStars,
                record.MobStars,
                BossIcon = record.BossInfo.Icon,
                Value = JsonSerializer.Serialize(data, AppConfig.JsonSerializerOptions),
            };
            dapper.Execute("""
                INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                """, obj);
        }

        data = await ExecuteWithRequestRecoveryAsync(role, client => client.GetStarRailChallengePeakDataAsync(role, 3, cancellationToken), cancellationToken);
        foreach (var record in data.ChallengePeakRecords.ToList())
        {
            data.ChallengePeakRecords.Clear();
            var queryData = dapper.QueryFirstOrDefault<ChallengePeakData>("""
                SELECT BossStars, MobStars FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @GroupId LIMIT 1;
                """, new { role.Uid, record.Group.GroupId });
            if (queryData is null)
            {
                data.ChallengePeakRecords.Add(record);
                data.ChallengePeakBestRecordBrief = new ChallengePeakBestRecordBrief
                {
                    BossStars = record.BossStars,
                    MobStars = record.MobStars,
                };
                var obj = new
                {
                    role.Uid,
                    record.Group.GroupId,
                    record.Group.GameVersion,
                    record.BossStars,
                    record.MobStars,
                    BossIcon = record.BossInfo.Icon,
                    Value = JsonSerializer.Serialize(data, AppConfig.JsonSerializerOptions),
                };
                dapper.Execute("""
                    INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                    VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                    """, obj);
            }
            else if (record.BossStars > queryData.BossStars || record.MobStars > queryData.MobStars)
            {
                var queryText = dapper.QueryFirstOrDefault<string>("""
                    SELECT Value FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @GroupId LIMIT 1;
                    """, new { role.Uid, record.Group.GroupId });
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    var queryValue = JsonSerializer.Deserialize<ChallengePeakData>(queryText);
                    if (queryValue is not null)
                    {
                        queryValue.ChallengePeakRecords.Clear();
                        queryValue.ChallengePeakRecords.Add(record);
                        queryValue.ChallengePeakBestRecordBrief ??= new();
                        queryValue.ChallengePeakBestRecordBrief.BossStars = record.BossStars;
                        queryValue.ChallengePeakBestRecordBrief.MobStars = record.MobStars;

                        var obj = new
                        {
                            role.Uid,
                            record.Group.GroupId,
                            record.Group.GameVersion,
                            record.BossStars,
                            record.MobStars,
                            BossIcon = record.BossInfo.Icon,
                            Value = JsonSerializer.Serialize(queryValue),
                        };
                        dapper.Execute("""
                            INSERT OR REPLACE INTO StarRailChallengePeakData (Uid, GroupId, GameVersion, BossStars, MobStars, BossIcon, Value)
                            VALUES (@Uid, @GroupId, @GameVersion, @BossStars, @MobStars, @BossIcon, @Value);
                            """, obj);
                    }
                }
            }
        }
    }



    public ChallengePeakData? GetStarRailChallengePeakData(GameRecordRole role, int groupId)
    {
        using var dapper = DatabaseService.CreateConnection();
        var queryText = dapper.QueryFirstOrDefault<string>("""
                    SELECT Value FROM StarRailChallengePeakData WHERE Uid = @Uid AND GroupId = @groupId LIMIT 1;
                    """, new { role.Uid, groupId });
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            return JsonSerializer.Deserialize<ChallengePeakData>(queryText);
        }
        return null;
    }




    #endregion




    #region Sign In


    /// <summary>
    /// 签到前准备：国服角色同步设备指纹。
    /// 请求本身按角色选 CN/OS Client（见 <see cref="GetClient"/>），故不改共享的 <see cref="IsHoyolab"/>——
    /// 自动签到常驻在后台跑，改那个字段会串到界面正在进行的账号操作上。
    /// </summary>
    /// <param name="role">游戏角色，用于判断 global / cn。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PrepareSignInClientAsync(GameRecordRole role, CancellationToken cancellationToken)
    {
        if (!IsGlobalServerRole(role))
        {
            await UpdateHyperionDeviceFpWithLockAsync(false, cancellationToken);
        }
    }


    /// <summary>
    /// 本月签到奖励列表。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当月每日奖励。</returns>
    public async Task<SignInReward> GetSignInRewardAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        await PrepareSignInClientAsync(role, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GetSignInRewardAsync(role, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 当前签到状态。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已签天数、今日是否已签等。</returns>
    public async Task<SignInRewardInfo> GetSignInInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        await PrepareSignInClientAsync(role, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GetSignInInfoAsync(role, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 补签信息。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签次数与货币消耗。</returns>
    public async Task<SignInResignInfo> GetSignInResignInfoAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        await PrepareSignInClientAsync(role, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.GetSignInResignInfoAsync(role, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 执行今日签到。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签到结果。</returns>
    public async Task<SignInResult> SignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        await PrepareSignInClientAsync(role, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.SignInAsync(role, cancellationToken), cancellationToken);
    }


    /// <summary>
    /// 执行补签。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补签结果。</returns>
    public async Task<SignInResult> ReSignInAsync(GameRecordRole role, CancellationToken cancellationToken = default)
    {
        await PrepareSignInClientAsync(role, cancellationToken);
        return await ExecuteWithRequestRecoveryAsync(role, client => client.ReSignInAsync(role, cancellationToken), cancellationToken);
    }


    #endregion


}
