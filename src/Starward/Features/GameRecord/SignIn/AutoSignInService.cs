using Dapper;
using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Features.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord.SignIn;

/// <summary>
/// 自动签到：启动后先批量签一轮，之后常驻循环在 UTC+8 日界后再次签到。
/// 是否签到按游戏区分（<see cref="AppConfig.GetAutoSignInEnabled(GameBiz)"/>）；
/// 日界以 info 的 <c>Today</c> 为准，本机时钟只负责排期；失败冷却 10 分钟避免出错时反复请求。
/// </summary>
internal class AutoSignInService
{

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 启动后先缓冲一段时间再开始批量签到，避开启动高峰。
    /// </summary>
    private static readonly TimeSpan StartupBatchDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 长等待拆段的上限。相对 Delay 不计休眠，分段醒来用墙上时钟重算剩余。
    /// </summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(15);

    /// <summary>当天仍有角色没签成（断网等）时的重试间隔。</summary>
    private static readonly TimeSpan IncompleteRetry = TimeSpan.FromMinutes(30);

    /// <summary>Cookie 失效 / 风控：拉长间隔，避免白天连打。</summary>
    private static readonly TimeSpan BlockedRetry = TimeSpan.FromHours(2);

    /// <summary>
    /// 相邻两个签到请求之间的随机间隔（秒），模拟真人操作节奏，避免短时间内大量请求。
    /// </summary>
    private const int MinRequestDelaySeconds = 3;
    private const int MaxRequestDelaySeconds = 8;

    private const int MinEarlyRetryMinutes = 10;
    private const int MaxEarlyRetryMinutes = 20;

    /// <summary>
    /// 连续 Early 的短重试次数上限。本机时钟快于签到服务器日界时（用户手动调快时间等）每轮都会 Early，
    /// 不设上限就会整日 10–20 分钟轮询一次；超过后退化成当天重试间隔。
    /// </summary>
    private const int MaxConsecutiveEarlyRetries = 3;

    private const int MinResumeStaggerSeconds = 10;
    private const int MaxResumeStaggerSeconds = 90;


    private readonly ILogger<AutoSignInService> _logger;

    private readonly GameRecordService _gameRecordService;

    private readonly SignInService _signInService;

    /// <summary>保证常驻循环每个进程只启动一次。</summary>
    private int _residentStarted;

    /// <summary>用户打开自动签到开关：必须带此标志，只 Wake 时 nextDue 可能已排到明天。</summary>
    private int _forceCheck;

    /// <summary>系统从休眠恢复。到点后的批量前用于错峰，未到点的唤醒会清掉以免误延后。</summary>
    private int _resumed;

    private readonly SemaphoreSlim _batchGate = new(1, 1);

    private readonly SemaphoreSlim _wake = new(0, 1);


    /// <summary>
    /// 初始化自动签到服务。
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <param name="gameRecordService">战绩门面，用于枚举角色与查询签到状态。</param>
    /// <param name="signInService">签到业务服务，执行实际签到请求。</param>
    public AutoSignInService(ILogger<AutoSignInService> logger, GameRecordService gameRecordService, SignInService signInService)
    {
        _logger = logger;
        _gameRecordService = gameRecordService;
        _signInService = signInService;
    }


    /// <summary>
    /// 指定游戏是否开启自动签到（按游戏区分）。
    /// </summary>
    /// <param name="biz">游戏业务线，如 hk4e_cn。</param>
    /// <returns>是否已开启自动签到。</returns>
    public bool IsEnabled(GameBiz biz) => AppConfig.GetAutoSignInEnabled(biz);

    /// <summary>
    /// 设置指定游戏的自动签到开关（按游戏区分）。
    /// </summary>
    /// <param name="biz">游戏业务线。</param>
    /// <param name="value">是否开启。</param>
    public void SetEnabled(GameBiz biz, bool value) => AppConfig.SetAutoSignInEnabled(biz, value);


    /// <summary>
    /// 启动常驻循环：先缓冲再签一轮，之后按绝对到期时刻重复。幂等。
    /// </summary>
    public void StartResident()
    {
        if (Interlocked.Exchange(ref _residentStarted, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() => RunResidentLoopAsync(CancellationToken.None));
    }


    /// <summary>
    /// 打开自动签到开关后立即检查。必须置 ForceCheck，否则循环可能按已排到明天的 nextDue 继续睡。
    /// </summary>
    public void RequestImmediateCheck()
    {
        Interlocked.Exchange(ref _forceCheck, 1);
        Wake();
    }


    /// <summary>
    /// 账号重新登录 / Cookie 更新后调用：清掉这些角色的失败冷却，并立即检查一轮。
    /// Cookie 失效会把下一轮排到 2 小时后，重新登录本身改不到这个排期；
    /// 且只叫醒不清冷却的话，10 分钟内重新登录会被冷却直接跳过，等于没叫。
    /// </summary>
    /// <param name="roles">Cookie 刚更新的角色。</param>
    public void NotifyRolesReauthenticated(IEnumerable<GameRecordRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        List<string> failureKeys = roles.Where(r => GameFeatureConfig.FromGameBiz(r.GameBiz).SupportSignIn)
                                        .Select(GetFailureKey)
                                        .ToList();
        if (failureKeys.Count == 0)
        {
            return;
        }
        try
        {
            ClearFailureCooldowns(failureKeys);
        }
        catch (Exception ex)
        {
            // 清冷却失败也要照常叫醒：最差只是这一次仍被冷却挡住，等下一轮
            _logger.LogWarning(ex, "Auto sign-in: clear failure cooldown failed.");
        }
        _logger.LogInformation("Auto sign-in re-check requested after re-login ({count} role(s)).", failureKeys.Count);
        RequestImmediateCheck();
    }


    /// <summary>
    /// 系统从休眠/休眠到磁盘恢复。只唤醒等待去重算绝对时刻，未到点不跑批量。
    /// </summary>
    public void NotifySystemResumed()
    {
        Interlocked.Exchange(ref _resumed, 1);
        Wake();
    }



    /// <summary>
    /// 常驻循环：启动缓冲 → 立刻跑一轮 → 按聚合结果排 nextDue。
    /// 整轮（等待、错峰、批量）异常都只影响本轮并排下一次，不能让常驻签到在本进程内静默死掉。
    /// </summary>
    private async Task RunResidentLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WaitOrWakeAsync(StartupBatchDelay, cancellationToken);
            DateTimeOffset nextDue = DateTimeOffset.UtcNow;
            int consecutiveEarly = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                AutoSignInRoundOutcome outcome;
                try
                {
                    outcome = await RunRoundAsync(nextDue, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto sign-in round failed.");
                    outcome = AutoSignInRoundOutcome.Incomplete;
                }

                consecutiveEarly = outcome is AutoSignInRoundOutcome.Early ? consecutiveEarly + 1 : 0;
                if (consecutiveEarly == MaxConsecutiveEarlyRetries + 1)
                {
                    _logger.LogWarning("Auto sign-in early retry limit reached; local clock is likely ahead of the sign-in server.");
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                nextDue = outcome switch
                {
                    AutoSignInRoundOutcome.Early when consecutiveEarly <= MaxConsecutiveEarlyRetries => now + RandomEarlyRetry(),
                    // 连续问早太多次说明本机时钟偏快，不是服务器晚翻天：退回当天重试间隔，别再密集轮询
                    AutoSignInRoundOutcome.Early => SignInSchedule.GetRetryOrNextDay(now, IncompleteRetry),
                    AutoSignInRoundOutcome.Incomplete => SignInSchedule.GetRetryOrNextDay(now, IncompleteRetry),
                    AutoSignInRoundOutcome.Blocked => SignInSchedule.GetRetryOrNextDay(now, BlockedRetry),
                    _ => SignInSchedule.GetNextDailyDue(now),
                };
                _logger.LogInformation("Auto sign-in next due {due} (outcome {outcome}).", nextDue, outcome);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto sign-in resident loop failed.");
        }
    }


    /// <summary>
    /// 一轮：等到 <paramref name="nextDue"/>（或 ForceCheck）→ 休眠唤醒错峰 → 跑批量。
    /// </summary>
    /// <param name="nextDue">本轮的到期时刻。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮聚合结果。</returns>
    private async Task<AutoSignInRoundOutcome> RunRoundAsync(DateTimeOffset nextDue, CancellationToken cancellationToken)
    {
        bool force = await WaitUntilDueAsync(nextDue, cancellationToken);
        bool resumed = Interlocked.Exchange(ref _resumed, 0) == 1;
        if (resumed && !force && DateTimeOffset.UtcNow >= nextDue)
        {
            int seconds = Random.Shared.Next(MinResumeStaggerSeconds, MaxResumeStaggerSeconds + 1);
            _logger.LogInformation("Auto sign-in resume stagger {seconds}s.", seconds);
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        return await RunBatchAsync(cancellationToken);
    }


    /// <summary>
    /// 等到 <paramref name="nextDue"/>，或收到 ForceCheck。分段 Delay，每次醒来用墙上时钟重算。
    /// </summary>
    /// <returns>因 ForceCheck 跳出时为 true。</returns>
    private async Task<bool> WaitUntilDueAsync(DateTimeOffset nextDue, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (Interlocked.Exchange(ref _forceCheck, 0) == 1)
            {
                return true;
            }
            if (now >= nextDue)
            {
                return false;
            }
            TimeSpan wait = nextDue - now;
            if (wait > WatchdogInterval)
            {
                wait = WatchdogInterval;
            }
            await WaitOrWakeAsync(wait, cancellationToken);
            // 没到点的唤醒只用于重算剩余，不要把 Resume 标志留到真正到期再错峰
            if (DateTimeOffset.UtcNow < nextDue)
            {
                Interlocked.Exchange(ref _resumed, 0);
            }
        }
    }


    /// <summary>
    /// <see cref="Task.Delay"/> 与唤醒信号竞速。任一方完成后取消另一半，避免 Delay 泄漏。
    /// 若 Delay 先赢而恰好同时 Release，permit 留在信号量里，下一圈立即返回——多一次早重算，无害。
    /// </summary>
    private async Task WaitOrWakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delayTask = Task.Delay(wait, linked.Token);
        Task wakeTask = _wake.WaitAsync(linked.Token);
        Task completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 被 linked 取消的那一半；赢家已完成
        }
    }


    /// <summary>唤醒等待中的循环。信号量已满时忽略（已有一次待处理唤醒）。</summary>
    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signaled
        }
    }


    /// <summary>
    /// 跑一轮批量签到。入口串行化，防止 ForceCheck 与循环重叠进入。
    /// </summary>
    private async Task<AutoSignInRoundOutcome> RunBatchAsync(CancellationToken cancellationToken)
    {
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunBatchCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _batchGate.Release();
        }
    }


    /// <summary>
    /// 遍历所有支持签到的角色：先 GET info，未签再 POST。聚合 Early &gt; Incomplete &gt; Blocked &gt; Completed。
    /// </summary>
    private async Task<AutoSignInRoundOutcome> RunBatchCoreAsync(CancellationToken cancellationToken)
    {
        List<GameRecordRole> roles;
        try
        {
            roles = _gameRecordService.GetAllGameRoles()
                .Where(r => GameFeatureConfig.FromGameBiz(r.GameBiz).SupportSignIn)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto sign-in batch: load game roles failed.");
            return AutoSignInRoundOutcome.Incomplete;
        }
        if (roles.Count == 0)
        {
            return AutoSignInRoundOutcome.Completed;
        }

        DateOnly expectedDate = SignInSchedule.GetServerDate(DateTimeOffset.UtcNow);
        _logger.LogInformation("Auto sign-in batch started for {count} role(s), expected date {date}.", roles.Count, expectedDate);

        bool pacingStarted = false;
        async Task PaceAsync(CancellationToken token)
        {
            if (pacingStarted)
            {
                int seconds = Random.Shared.Next(MinRequestDelaySeconds, MaxRequestDelaySeconds + 1);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
            else
            {
                pacingStarted = true;
            }
        }

        bool sawIncomplete = false;
        bool sawBlocked = false;
        bool sawSignedPreviousDay = false;
        foreach (GameRecordRole role in roles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEnabled(role.GameBiz))
            {
                continue;
            }
            AutoSignInRoleResult result;
            try
            {
                result = await SignInRoleCoreAsync(role, expectedDate, PaceAsync, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto sign-in batch: role failed (biz {biz}, uid {uid}).", role.GameBiz, role.Uid);
                result = AutoSignInRoleResult.Failed;
            }
            if (result is AutoSignInRoleResult.Early)
            {
                // 已签 + 服务器没翻天：本轮确实没事可做。此时其余角色多半也已签过，不再逐个白问
                _logger.LogInformation("Auto sign-in batch aborted early (server date not rolled).");
                return AutoSignInRoundOutcome.Early;
            }
            if (result is AutoSignInRoleResult.SignedPreviousDay)
            {
                // 与上面相反：能签成说明昨天本来就漏着，其余角色很可能同样漏着，必须跑完再收尾
                sawSignedPreviousDay = true;
            }
            else if (result is AutoSignInRoleResult.Failed or AutoSignInRoleResult.Cooldown)
            {
                sawIncomplete = true;
            }
            else if (result is AutoSignInRoleResult.Blocked)
            {
                sawBlocked = true;
            }
        }

        // 排在最前：这一轮签掉的是上一天，必须用最短的重试接住几分钟后才开始的新一天
        if (sawSignedPreviousDay)
        {
            return AutoSignInRoundOutcome.Early;
        }
        if (sawIncomplete)
        {
            return AutoSignInRoundOutcome.Incomplete;
        }
        if (sawBlocked)
        {
            return AutoSignInRoundOutcome.Blocked;
        }
        return AutoSignInRoundOutcome.Completed;
    }


    /// <summary>
    /// 单个角色：冷却 → 查询是否已签 / 是否翻天 → 未签则签到。
    /// 请求抛出的异常在此收口：一律记录失败时间进冷却，Cookie 失效与 POST 侧的
    /// <see cref="SignInActionResult.CookieExpired"/> 同权按 <see cref="AutoSignInRoleResult.Blocked"/> 处理。
    /// </summary>
    /// <param name="role">待签到的游戏角色。</param>
    /// <param name="expectedDate">本轮开始时本机推算的 UTC+8 日期。</param>
    /// <param name="pace">请求节奏控制委托，在每次 API 调用前执行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该角色对本轮聚合的贡献。</returns>
    private async Task<AutoSignInRoleResult> SignInRoleCoreAsync(
        GameRecordRole role,
        DateOnly expectedDate,
        Func<CancellationToken, Task> pace,
        CancellationToken cancellationToken)
    {
        string failureKey = GetFailureKey(role);
        if (IsInFailureCooldown(failureKey))
        {
            return AutoSignInRoleResult.Cooldown;
        }
        try
        {
            return await SignInRoleRequestAsync(role, expectedDate, failureKey, pace, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 抛异常的请求（查询接口失败、断网）以前不写失败时间，等于不进冷却，下一轮会立刻重来
            SetSettingValue(failureKey, DateTimeOffset.UtcNow.Ticks.ToString());
            bool loginExpired = ex is miHoYoApiException { IsLoginExpired: true };
            _logger.LogWarning(ex, "Auto sign-in request failed (biz {biz}, uid {uid}, login expired {expired}).", role.GameBiz, role.Uid, loginExpired);
            // Cookie 失效不会自己恢复，退到 BlockedRetry 的长间隔；断网等临时故障仍按当天重试
            return loginExpired ? AutoSignInRoleResult.Blocked : AutoSignInRoleResult.Failed;
        }
    }


    /// <summary>
    /// 查询签到状态，未签则签到。异常不在此处理，交由 <see cref="SignInRoleCoreAsync"/> 收口。
    /// </summary>
    /// <param name="role">待签到的游戏角色。</param>
    /// <param name="expectedDate">本轮开始时本机推算的 UTC+8 日期。</param>
    /// <param name="failureKey">该角色记录失败时间的 Setting 键。</param>
    /// <param name="pace">请求节奏控制委托，在每次 API 调用前执行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该角色对本轮聚合的贡献。</returns>
    private async Task<AutoSignInRoleResult> SignInRoleRequestAsync(
        GameRecordRole role,
        DateOnly expectedDate,
        string failureKey,
        Func<CancellationToken, Task> pace,
        CancellationToken cancellationToken)
    {
        await pace(cancellationToken);
        var info = await _gameRecordService.GetSignInInfoAsync(role, cancellationToken);
        bool rolled = SignInSchedule.HasServerDateRolled(info.Today, expectedDate);
        if (info.IsSign)
        {
            SetSettingValue(failureKey, "0");
            if (!rolled)
            {
                _logger.LogInformation(
                    "Auto sign-in asked early (biz {biz}, uid {uid}, today {today}, expected {expected}).",
                    role.GameBiz, role.Uid, info.Today, expectedDate);
                return AutoSignInRoleResult.Early;
            }
            return AutoSignInRoleResult.AlreadySigned;
        }

        await pace(cancellationToken);
        SignInActionResponse result = await _signInService.ClaimSignInAsync(role, cancellationToken);
        if (result.Kind is SignInActionResult.Success or SignInActionResult.AlreadySigned)
        {
            SetSettingValue(failureKey, "0");
            _logger.LogInformation("Auto sign-in succeeded (biz {biz}, uid {uid}, result {result}).", role.GameBiz, role.Uid, result);
            if (!rolled)
            {
                // 本机已过 0 点但服务器还没翻天：这一签落在上一天（该签，是它最后的机会），
                // 但本轮绝不能算完成——否则会睡到明天 0 点，把几分钟后才开始的今天整天漏掉
                _logger.LogInformation(
                    "Auto sign-in signed the previous server day (biz {biz}, uid {uid}, today {today}, expected {expected}).",
                    role.GameBiz, role.Uid, info.Today, expectedDate);
                return AutoSignInRoleResult.SignedPreviousDay;
            }
            return AutoSignInRoleResult.Signed;
        }

        SetSettingValue(failureKey, DateTimeOffset.UtcNow.Ticks.ToString());
        _logger.LogInformation("Auto sign-in not completed (biz {biz}, uid {uid}, result {result}).", role.GameBiz, role.Uid, result);
        if (result.Kind is SignInActionResult.CookieExpired or SignInActionResult.RiskControl)
        {
            return AutoSignInRoleResult.Blocked;
        }
        return AutoSignInRoleResult.Failed;
    }


    /// <summary>
    /// 该角色记录失败时间的 Setting 键，按 biz + uid 区分。
    /// </summary>
    /// <param name="role">游戏角色。</param>
    /// <returns>Setting 表键名。</returns>
    private static string GetFailureKey(GameRecordRole role) => $"auto_sign_in_last_failure_ticks_{role.GameBiz}_{role.Uid}";


    /// <summary>
    /// 批量清零失败时间，让这些角色不再被冷却挡住。
    /// </summary>
    /// <param name="failureKeys">要清零的 Setting 键。</param>
    private static void ClearFailureCooldowns(IReadOnlyCollection<string> failureKeys)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, '0');", failureKeys.Select(key => new { key }));
    }


    /// <summary>
    /// 检查角色是否处于失败冷却期，避免短时间内重复请求。
    /// </summary>
    /// <param name="failureKey">Setting 表键名，按 biz + uid 区分。</param>
    /// <returns>冷却未结束时返回 true。</returns>
    private static bool IsInFailureCooldown(string failureKey)
    {
        if (long.TryParse(GetSettingValue(failureKey), out long ticks) && ticks != 0)
        {
            var lastFailure = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - lastFailure < FailureCooldown)
            {
                return true;
            }
        }
        return false;
    }


    private static TimeSpan RandomEarlyRetry()
    {
        return TimeSpan.FromMinutes(Random.Shared.Next(MinEarlyRetryMinutes, MaxEarlyRetryMinutes + 1));
    }


    /// <summary>从 Setting 表读取键值。</summary>
    /// <param name="key">设置键。</param>
    /// <returns>存储的字符串，不存在时返回 null。</returns>
    private static string? GetSettingValue(string key)
    {
        using var dapper = DatabaseService.CreateConnection();
        return dapper.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key = @key LIMIT 1;", new { key });
    }


    /// <summary>写入 Setting 表键值（INSERT OR REPLACE）。</summary>
    /// <param name="key">设置键。</param>
    /// <param name="value">设置值。</param>
    private static void SetSettingValue(string key, string value)
    {
        using var dapper = DatabaseService.CreateConnection();
        dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @value);", new { key, value });
    }

}



/// <summary>一轮批量对调度的聚合结果。优先级 Early &gt; Incomplete &gt; Blocked &gt; Completed。</summary>
internal enum AutoSignInRoundOutcome
{
    /// <summary>启用的角色都已签，或没有可签角色。排下一个 UTC+8 0:00 + jitter。</summary>
    Completed,

    /// <summary>
    /// 服务器日期尚未翻天：问早了（本轮其余角色不再请求），或刚签掉的是上一天（其余角色照常跑完）。短重试。
    /// </summary>
    Early,

    /// <summary>仍有角色因断网、通用失败或冷却未签成。当天 30 分钟再试。</summary>
    Incomplete,

    /// <summary>仅剩 Cookie 失效 / 风控。当天 2 小时再试，避免连打。</summary>
    Blocked,
}


/// <summary>单个角色对本轮聚合的贡献。</summary>
internal enum AutoSignInRoleResult
{
    Cooldown,

    /// <summary>已签，且服务器日期还没翻天：问早了。</summary>
    Early,

    AlreadySigned,

    Signed,

    /// <summary>签成功，但服务器日期还没翻天，签掉的是上一天：本轮不能算完成，须短重试接住新的一天。</summary>
    SignedPreviousDay,

    Failed,

    Blocked,
}
