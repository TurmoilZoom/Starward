using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord.Passport;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameRecord;

/// <summary>
/// 米游社短信验证码登录编排：发码 / 登录（含 aigis 极验回调）/ 换票拼 Cookie。
/// </summary>
internal class CaptchaLoginService
{

    /// <summary>
    /// 当 passport 返回 aigis 风控时，由 UI 完成极验并返回 <c>x-rpc-aigis</c> 头值；取消或失败返回 null。
    /// </summary>
    /// <param name="aigis">服务端下发的 aigis 载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的 aigis 请求头；用户取消或失败时为 null。</returns>
    public delegate Task<string?> ResolveAigisAsync(CaptchaAigis aigis, CancellationToken cancellationToken);


    private readonly ILogger<CaptchaLoginService> _logger;
    private readonly MihoyoPassportClient _passportClient;
    private readonly GameRecordService _gameRecordService;


    /// <summary>
    /// 初始化验证码登录服务。
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <param name="passportClient">passport 客户端。</param>
    /// <param name="gameRecordService">GameRecord 门面（设备指纹等）。</param>
    public CaptchaLoginService(
        ILogger<CaptchaLoginService> logger,
        MihoyoPassportClient passportClient,
        GameRecordService gameRecordService)
    {
        _logger = logger;
        _passportClient = passportClient;
        _gameRecordService = gameRecordService;
    }


    /// <summary>
    /// 发送登录短信验证码；遇 aigis 时通过 <paramref name="resolveAigis"/> 完成极验后重试。
    /// </summary>
    /// <param name="phone">11 位国区手机号。</param>
    /// <param name="resolveAigis">极验回调；不可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>含 <c>action_type</c> 与建议倒计时的发码结果。</returns>
    /// <exception cref="ArgumentException">手机号格式非法。</exception>
    /// <exception cref="miHoYoApiException">业务失败或用户取消极验。</exception>
    /// <exception cref="OperationCanceledException">用户取消极验或令牌取消。</exception>
    public async Task<CreateLoginCaptchaResult> CreateCaptchaAsync(
        string phone,
        ResolveAigisAsync resolveAigis,
        CancellationToken cancellationToken = default)
    {
        EnsureValidPhone(phone);
        ArgumentNullException.ThrowIfNull(resolveAigis);
        await PrepareDeviceAsync(cancellationToken);

        string? aigisHeader = null;
        // 极验可能连续触发，限制重试避免死循环（与 TeyvatGuide 递归重试等价）
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _passportClient.CreateLoginCaptchaAsync(phone, aigisHeader, cancellationToken);
            if (result.IsSuccess && result.Data is not null && !string.IsNullOrWhiteSpace(result.Data.ActionType))
            {
                _logger.LogInformation(
                    "Login captcha sent for phone ending {suffix}, countdown={countdown}, sent_new={sentNew}.",
                    phone[^4..],
                    result.Data.Countdown,
                    result.Data.SentNew);
                return result.Data;
            }

            // TeyvatGuide：retcode != 0 时优先读响应头 x-rpc-aigis 做极验再重试
            if (result.Aigis is not null)
            {
                _logger.LogInformation("createLoginCaptcha requires aigis (attempt {attempt}).", attempt + 1);
                aigisHeader = await resolveAigis(result.Aigis, cancellationToken);
                if (string.IsNullOrWhiteSpace(aigisHeader))
                {
                    throw new OperationCanceledException("Geetest verification was cancelled.");
                }
                continue;
            }

            _logger.LogWarning("createLoginCaptcha failed: {retcode} {message}", result.Retcode, result.Message);
            throw new miHoYoApiException(result.Retcode, result.Message);
        }

        throw new miHoYoApiException(-1, "Aigis verification failed too many times.");
    }


    /// <summary>
    /// 使用短信验证码登录，换齐 ltoken / cookie_token 后返回 Cookie 字符串。
    /// </summary>
    /// <param name="phone">11 位国区手机号。</param>
    /// <param name="captcha">短信验证码。</param>
    /// <param name="actionType"><see cref="CreateCaptchaAsync"/> 返回的 action_type。</param>
    /// <param name="resolveAigis">极验回调；不可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可供 <see cref="GameRecordService.AddRecordUserAsync"/> 使用的 Cookie 串。</returns>
    /// <exception cref="ArgumentException">参数非法。</exception>
    /// <exception cref="miHoYoApiException">业务失败。</exception>
    /// <exception cref="OperationCanceledException">用户取消极验或令牌取消。</exception>
    public async Task<string> LoginByCaptchaAsync(
        string phone,
        string captcha,
        string actionType,
        ResolveAigisAsync resolveAigis,
        CancellationToken cancellationToken = default)
    {
        EnsureValidPhone(phone);
        if (string.IsNullOrWhiteSpace(captcha))
        {
            throw new ArgumentException("Captcha code is required.", nameof(captcha));
        }
        if (string.IsNullOrWhiteSpace(actionType))
        {
            throw new ArgumentException("Action type is required.", nameof(actionType));
        }
        ArgumentNullException.ThrowIfNull(resolveAigis);
        await PrepareDeviceAsync(cancellationToken);

        string? aigisHeader = null;
        LoginByMobileCaptchaResult? login = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _passportClient.LoginByMobileCaptchaAsync(phone, captcha.Trim(), actionType, aigisHeader, cancellationToken);
            if (result.IsSuccess && result.Data is not null)
            {
                login = result.Data;
                break;
            }

            if (result.Aigis is not null)
            {
                _logger.LogInformation("loginByMobileCaptcha requires aigis (attempt {attempt}).", attempt + 1);
                aigisHeader = await resolveAigis(result.Aigis, cancellationToken);
                if (string.IsNullOrWhiteSpace(aigisHeader))
                {
                    throw new OperationCanceledException("Geetest verification was cancelled.");
                }
                continue;
            }

            _logger.LogWarning("loginByMobileCaptcha failed: {retcode} {message}", result.Retcode, result.Message);
            throw new miHoYoApiException(result.Retcode, result.Message);
        }

        if (login?.Token?.Token is null || login.UserInfo is null)
        {
            throw new miHoYoApiException(-1, "Login response is incomplete.");
        }

        string stoken = login.Token.Token;
        string aid = login.UserInfo.Aid;
        string mid = login.UserInfo.Mid;
        if (string.IsNullOrWhiteSpace(stoken) || string.IsNullOrWhiteSpace(aid) || string.IsNullOrWhiteSpace(mid))
        {
            throw new miHoYoApiException(-1, "Login response is incomplete.");
        }

        _logger.LogInformation("Captcha login succeeded for aid {aid}, exchanging tokens.", aid);

        string ltoken = await _passportClient.GetLTokenBySTokenAsync(stoken, mid, cancellationToken);
        string cookieToken = await _passportClient.GetCookieTokenBySTokenAsync(stoken, mid, cancellationToken);
        return MihoyoPassportClient.BuildCookieString(aid, mid, stoken, ltoken, cookieToken);
    }


    /// <summary>
    /// 校验国区手机号格式（1 开头 11 位）。
    /// </summary>
    /// <param name="phone">手机号。</param>
    /// <returns>合法时为 true。</returns>
    public static bool IsValidPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Length != 11)
        {
            return false;
        }
        if (phone[0] != '1' || phone[1] < '3' || phone[1] > '9')
        {
            return false;
        }
        for (int i = 2; i < 11; i++)
        {
            if (phone[i] < '0' || phone[i] > '9')
            {
                return false;
            }
        }
        return true;
    }


    /// <summary>
    /// 同步设备指纹到 passport 客户端。
    /// </summary>
    private async Task PrepareDeviceAsync(CancellationToken cancellationToken)
    {
        // 短信验证码登录仅国服：显式走国服指纹，别让共享的 IsHoyolab 把它跳过
        await _gameRecordService.EnsureHyperionDeviceFpAsync(false, cancellationToken);
        // 与 Hyperion 共用 AppConfig 中的设备 id / fp
        if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceId))
        {
            _passportClient.DeviceId = AppConfig.HyperionDeviceId;
        }
        if (!string.IsNullOrWhiteSpace(AppConfig.HyperionDeviceFp))
        {
            _passportClient.DeviceFp = AppConfig.HyperionDeviceFp;
        }
    }


    private static void EnsureValidPhone(string phone)
    {
        if (!IsValidPhone(phone))
        {
            throw new ArgumentException("Invalid mainland China mobile number.", nameof(phone));
        }
    }

}
