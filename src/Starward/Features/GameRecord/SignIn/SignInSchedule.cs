using System;
using System.Globalization;

namespace Starward.Features.GameRecord.SignIn;

/// <summary>
/// 自动签到调度用的纯计算：UTC+8 日界、下次 0 点、当天截止、服务器日期是否已翻天。
/// 不联网、不进 DI。本机时钟只用来排「何时去问」，是否该签仍看接口的 <c>IsSign</c> / <c>Today</c>。
/// </summary>
internal static class SignInSchedule
{

    /// <summary>网页/社区每日签到的日界时区（国服、国际服均为 UTC+8 当天 00:00）。</summary>
    public static readonly TimeSpan ServerOffset = TimeSpan.FromHours(8);

    /// <summary>Completed 后排到「下一个 0 点」时附加的随机分钟数（含端点）。</summary>
    public const int MinDailyJitterMinutes = 3;

    /// <summary>见 <see cref="MinDailyJitterMinutes"/>。</summary>
    public const int MaxDailyJitterMinutes = 8;


    /// <summary>
    /// 将 UTC 时刻换算成签到服务器日历日（UTC+8 的日期）。
    /// </summary>
    /// <param name="utcNow">UTC 时刻（<see cref="DateTimeOffset.UtcNow"/> 或测试注入）。</param>
    /// <returns>UTC+8 日历日。</returns>
    public static DateOnly GetServerDate(DateTimeOffset utcNow)
    {
        return DateOnly.FromDateTime(utcNow.ToOffset(ServerOffset).DateTime);
    }


    /// <summary>
    /// 下一个严格晚于 <paramref name="utcNow"/> 的 UTC+8 0:00，再加上 3–8 分钟抖动。
    /// <para>
    /// 23:50 签完应对准今晚的 0 点；00:30 签完应对准明天 0 点，不能再落到当天 0:00+jitter。
    /// </para>
    /// </summary>
    /// <param name="utcNow">UTC 时刻。</param>
    /// <returns>下次批量到期时刻（带 UTC+8 偏移，比较按绝对瞬间）。</returns>
    public static DateTimeOffset GetNextDailyDue(DateTimeOffset utcNow)
    {
        DateTimeOffset nextMidnight = GetNextMidnightUtcPlus8(utcNow);
        int jitterMinutes = Random.Shared.Next(MinDailyJitterMinutes, MaxDailyJitterMinutes + 1);
        return nextMidnight.AddMinutes(jitterMinutes);
    }


    /// <summary>
    /// 当天 UTC+8 23:00。Incomplete / Blocked 是否还在当天再试，用「调度时的 now」和它比，不用 now+重试间隔。
    /// </summary>
    /// <param name="utcNow">UTC 时刻。</param>
    /// <returns>当天 23:00（UTC+8）。</returns>
    public static DateTimeOffset GetSameDayCutoff(DateTimeOffset utcNow)
    {
        DateTimeOffset local = utcNow.ToOffset(ServerOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 23, 0, 0, ServerOffset);
    }


    /// <summary>
    /// 当天重试：现在已过 UTC+8 23:00，或重试时刻已跨过 0 点，都改排下一个 0 点+jitter；
    /// 否则 <paramref name="utcNow"/> + <paramref name="retry"/>。
    /// </summary>
    /// <param name="utcNow">调度时刻（UTC）。</param>
    /// <param name="retry">当天重试间隔。</param>
    /// <returns>下次到期时刻。</returns>
    public static DateTimeOffset GetRetryOrNextDay(DateTimeOffset utcNow, TimeSpan retry)
    {
        if (utcNow >= GetSameDayCutoff(utcNow))
        {
            return GetNextDailyDue(utcNow);
        }
        // 重试的语义是「当天再试一次」。跨过 0 点后它已经是明天那一轮，就得回到正常的 0 点+jitter：
        // 否则 22:30 的一次 Blocked 会把全部账号第二天的签到一起推到 00:30，
        // 单个账号（如 Cookie 失效）的失败不应该拖延其他账号的日程。
        if (utcNow + retry >= GetNextMidnightUtcPlus8(utcNow))
        {
            return GetNextDailyDue(utcNow);
        }
        return utcNow + retry;
    }


    /// <summary>
    /// 服务器日历是否已经翻到（或不早于）本机推算的 UTC+8「今天」。
    /// <paramref name="today"/> 为空或解析失败时视为已翻天，避免卡在 Early 短重试死循环。
    /// </summary>
    /// <param name="today">info 接口的 <c>Today</c>（yyyy-MM-dd）。</param>
    /// <param name="expected">本机推算的期望服务器日期。</param>
    /// <returns>已翻天或无法解析时为 true；服务器日期落后则为 false（问早了）。</returns>
    public static bool HasServerDateRolled(string? today, DateOnly expected)
    {
        if (string.IsNullOrEmpty(today))
        {
            return true;
        }
        if (!DateOnly.TryParse(today, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return true;
        }
        return parsed >= expected;
    }


    /// <summary>
    /// 下一个严格晚于 <paramref name="utcNow"/> 的 UTC+8 0:00。
    /// </summary>
    private static DateTimeOffset GetNextMidnightUtcPlus8(DateTimeOffset utcNow)
    {
        DateTimeOffset local = utcNow.ToOffset(ServerOffset);
        var todayMidnight = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, ServerOffset);
        return todayMidnight.AddDays(1);
    }

}
