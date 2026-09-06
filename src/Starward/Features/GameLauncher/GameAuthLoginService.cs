using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.GameRecord;
using Starward.Core.HoYoPlay;
using Starward.Features.GameRecord;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.GameLauncher;

public class GameAuthLoginService
{


    private readonly ILogger<GameAuthLoginService> _logger;


    private readonly HttpClient _httpClient;


    public GameAuthLoginService(ILogger<GameAuthLoginService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }



    private long? hyperionAid;



    public async Task<long?> GetHyperionAidAsync(CancellationToken cancellationToken = default)
    {
        if (!hyperionAid.HasValue)
        {
            await VerifyStokenAsync(cancellationToken);
        }
        return hyperionAid;
    }



    public async Task VerifyStokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AppConfig.stoken) || string.IsNullOrWhiteSpace(AppConfig.mid))
        {
            return;
        }
        long aid = await VerifyStokenCoreAsync(AppConfig.stoken, AppConfig.mid, refreshGlobalConfig: true, cancellationToken);
        if (aid > 0)
        {
            hyperionAid = aid;
        }
    }



    /// <summary>
    /// 使用全局 <see cref="AppConfig.stoken"/> / <see cref="AppConfig.mid"/> 换取 auth ticket（国服）。
    /// </summary>
    public async Task<string?> CreateAuthTicketByGameBiz(GameId gameId)
    {
        try
        {
            if (gameId.GameBiz.Server is not "cn")
            {
                return null;
            }
            CancellationToken cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token;
            long? aid = await GetHyperionAidAsync(cancellationToken);
            if (!hyperionAid.HasValue || string.IsNullOrWhiteSpace(AppConfig.stoken) || string.IsNullOrWhiteSpace(AppConfig.mid))
            {
                return null;
            }
            return await CreateAuthTicketCoreAsync(gameId, AppConfig.stoken, AppConfig.mid, hyperionAid.Value, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateAuthTicketByGameBiz");
        }
        return null;
    }


    /// <summary>
    /// 使用米游社工具箱角色 Cookie（stoken/mid/account_id）换取 auth ticket，用于按配置登录账号启动。
    /// </summary>
    /// <param name="gameId">目标游戏。</param>
    /// <param name="role">含有效 stoken 的国服角色；可为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>ticket；失败返回 null（不抛出）。</returns>
    public async Task<string?> CreateAuthTicketByGameRoleAsync(GameId gameId, GameRecordRole? role, CancellationToken cancellationToken = default)
    {
        try
        {
            if (gameId.GameBiz.Server is not "cn" || role is null || string.IsNullOrWhiteSpace(role.Cookie))
            {
                return null;
            }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationToken ct = timeoutCts.Token;

            Dictionary<string, string> cookies = GameRecordCookieRefreshService.ParseCookie(role.Cookie);
            if (!TryGetAuthCredentials(cookies, out string stoken, out string mid))
            {
                _logger.LogWarning("CreateAuthTicketByGameRole: cookie missing stoken/mid (game_uid={Uid}, biz={Biz})", role.Uid, role.GameBiz);
                return null;
            }

            long aid = 0;
            string accountIdText = GetFirstCookieValue(cookies, "account_id", "account_id_v2", "stuid", "ltuid", "ltuid_v2");
            if (!long.TryParse(accountIdText, out aid) || aid <= 0)
            {
                // Cookie 无 aid 时用 stoken 校验补全
                aid = await VerifyStokenCoreAsync(stoken, mid, refreshGlobalConfig: false, ct);
            }
            if (aid <= 0)
            {
                _logger.LogWarning("CreateAuthTicketByGameRole: cannot resolve passport aid (game_uid={Uid})", role.Uid);
                return null;
            }

            return await CreateAuthTicketCoreAsync(gameId, stoken, mid, aid, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateAuthTicketByGameRole (game_uid={Uid})", role?.Uid);
        }
        return null;
    }


    /// <summary>
    /// 调用 createAuthTicketByGameBiz。
    /// </summary>
    private async Task<string?> CreateAuthTicketCoreAsync(GameId gameId, string stoken, string mid, long aid, CancellationToken cancellationToken)
    {
        var obj = new
        {
            game_biz = gameId.GameBiz.ToString(),
            stoken,
            uid = aid,
            mid,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-verifier/app/createAuthTicketByGameBiz")
        {
            Content = JsonContent.Create(obj),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        request.Headers.Add("x-rpc-app_id", "ddxf5dufpuyo");
        request.Headers.Add("x-rpc-client_type", "3");
        request.Headers.Add("x-rpc-game_biz", "hyp_cn");
        // 固定国服接口：不能走会被 IsHoyolab 静默跳过的 UpdateDeviceFpAsync，否则指纹头可能是空的
        await AppConfig.GetService<GameRecordService>().EnsureHyperionDeviceFpAsync(false, cancellationToken);
        request.Headers.Add("x-rpc-device_fp", AppConfig.HyperionDeviceFp);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<Root<AuthTicket>>(cancellationToken);
        if (node!.Retcode != 0)
        {
            throw new miHoYoApiException(node.Retcode, node.Message);
        }
        return node.Data.Ticket;
    }


    /// <summary>
    /// 校验 stoken 并返回通行证 aid；可选回写全局 stoken。
    /// </summary>
    private async Task<long> VerifyStokenCoreAsync(string stoken, string mid, bool refreshGlobalConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stoken) || string.IsNullOrWhiteSpace(mid))
        {
            return 0;
        }
        var obj = new
        {
            token = new
            {
                token_type = 1,
                token = stoken,
            },
            refresh = true,
            mid,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://passport-api.mihoyo.com/account/ma-cn-session/app/verify")
        {
            Content = JsonContent.Create(obj),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        request.Headers.Add("x-rpc-app_id", "ddxf5dufpuyo");
        request.Headers.Add("x-rpc-client_type", "3");
        request.Headers.Add("x-rpc-game_biz", "hyp_cn");
        // 同上：固定国服接口
        await AppConfig.GetService<GameRecordService>().EnsureHyperionDeviceFpAsync(false, cancellationToken);
        request.Headers.Add("x-rpc-device_fp", AppConfig.HyperionDeviceFp);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<Root<Data>>(cancellationToken);
        if (node!.Retcode != 0)
        {
            throw new miHoYoApiException(node.Retcode, node.Message);
        }
        if (refreshGlobalConfig && node.Data.NewToken != null && node.Data.NewToken.Token != AppConfig.stoken)
        {
            AppConfig.stoken = node.Data.NewToken.Token;
            AppConfig.SaveConfiguration();
        }
        return node.Data.UserInfo.Aid;
    }


    private static bool TryGetAuthCredentials(Dictionary<string, string> cookies, out string stoken, out string mid)
    {
        stoken = GetFirstCookieValue(cookies, "stoken_v2", "stoken");
        mid = GetFirstCookieValue(cookies, "mid", "account_mid_v2", "ltmid_v2");
        return !string.IsNullOrWhiteSpace(stoken) && !string.IsNullOrWhiteSpace(mid);
    }


    private static string GetFirstCookieValue(Dictionary<string, string> cookies, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (cookies.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "";
    }




    public class Data
    {
        [JsonPropertyName("user_info")]
        public UserInfo UserInfo { get; set; }

        [JsonPropertyName("realname_info")]
        public object RealnameInfo { get; set; }

        [JsonPropertyName("need_realperson")]
        public bool NeedRealperson { get; set; }

        [JsonPropertyName("new_token")]
        public NewToken NewToken { get; set; }
    }

    public class NewToken
    {
        [JsonPropertyName("token_type")]
        public int TokenType { get; set; }

        [JsonPropertyName("token")]
        public string Token { get; set; }
    }

    public class Root<T>
    {
        [JsonPropertyName("retcode")]
        public int Retcode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public T Data { get; set; }
    }

    public class UserInfo
    {
        [JsonPropertyName("aid")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Aid { get; set; }

        [JsonPropertyName("mid")]
        public string Mid { get; set; }

        [JsonPropertyName("account_name")]
        public string AccountName { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("is_email_verify")]
        public int IsEmailVerify { get; set; }

        [JsonPropertyName("area_code")]
        public string AreaCode { get; set; }

        [JsonPropertyName("mobile")]
        public string Mobile { get; set; }

        [JsonPropertyName("safe_area_code")]
        public string SafeAreaCode { get; set; }

        [JsonPropertyName("safe_mobile")]
        public string SafeMobile { get; set; }

        [JsonPropertyName("realname")]
        public string Realname { get; set; }

        [JsonPropertyName("identity_code")]
        public string IdentityCode { get; set; }

        [JsonPropertyName("rebind_area_code")]
        public string RebindAreaCode { get; set; }

        [JsonPropertyName("rebind_mobile")]
        public string RebindMobile { get; set; }

        [JsonPropertyName("rebind_mobile_time")]
        public string RebindMobileTime { get; set; }

        [JsonPropertyName("links")]
        public List<object> Links { get; set; }

        [JsonPropertyName("country")]
        public string Country { get; set; }

        [JsonPropertyName("password_time")]
        public string PasswordTime { get; set; }

        [JsonPropertyName("unmasked_email")]
        public string UnmaskedEmail { get; set; }

        [JsonPropertyName("unmasked_email_type")]
        public int UnmaskedEmailType { get; set; }
    }


    public class AuthTicket
    {
        [JsonPropertyName("ticket")]
        public string Ticket { get; set; }
    }





}
