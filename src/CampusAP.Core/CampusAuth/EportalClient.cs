using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CampusAP.Core.CampusAuth;

public enum CampusAuthState
{
    Unknown, Checking, Online, PlainOnline, NeedLogin, PortalHijack, NoInternet,
}

/// <summary>门户类型（从 302 重定向 URL 自动识别）</summary>
public enum PortalKind
{
    /// <summary>无法识别，尝试通用 POST</summary>
    Unknown,
    /// <summary>锐捷 ePortal（InterFace.do?method=login）</summary>
    RuijieEportal,
    /// <summary>深澜 Srun（srun_portal）</summary>
    Srun,
    /// <summary>Dr.COM</summary>
    DrCom,
}

public record CampusAuthStatus(CampusAuthState State, string Detail, string? RedirectUrl = null, PortalKind Kind = PortalKind.Unknown);
public record CampusLoginResult(bool Success, string? Message);

/// <summary>
/// 通用校园网 Web 认证客户端。不写死门户地址和协议：
/// 1. 对中性 URL 发请求不跟随跳转：200 且内容匹配=在线；302=未认证，Location 即门户；
/// 2. 从重定向 URL 自动识别门户厂商（锐捷/深澜/Dr.COM）；
/// 3. 按厂商选择对应的登录接口和参数格式 POST。
/// 仅提交用户本人账密，不绕过验证码，不对抗共享检测。
/// </summary>
public sealed class EportalClient : IDisposable
{
    private const string CheckUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string CheckBodyMarker = "Microsoft Connect Test";

    private readonly HttpClient _http;

    public EportalClient()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        })
        { Timeout = TimeSpan.FromSeconds(6) };
    }

    public async Task<CampusAuthStatus> CheckAsync(bool hasCampusAccount, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(CheckUrl, ct);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!body.Contains(CheckBodyMarker, StringComparison.OrdinalIgnoreCase))
                    return new CampusAuthStatus(CampusAuthState.PortalHijack,
                        "HTTP 请求被网关替换内容（无跳转地址可解析），请在浏览器手动认证");

                // 能直连上网：配了校园账号=认证后在线，没配=普通网络
                return hasCampusAccount
                    ? new CampusAuthStatus(CampusAuthState.Online, "校园网认证有效，网络连通正常")
                    : new CampusAuthStatus(CampusAuthState.PlainOnline, "普通网络，此模块未激活");
            }

            if (resp.Headers.Location is not null)
            {
                var redirect = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(CheckUrl), resp.Headers.Location);
                var kind = DetectPortalKind(redirect.ToString());
                var kindName = kind switch
                {
                    PortalKind.RuijieEportal => "锐捷 ePortal",
                    PortalKind.Srun => "深澜 Srun",
                    PortalKind.DrCom => "Dr.COM",
                    _ => "未知门户",
                };
                return new CampusAuthStatus(CampusAuthState.NeedLogin,
                    $"未认证（{kindName} · {redirect.Authority}）", redirect.ToString(), kind);
            }

            return new CampusAuthStatus(CampusAuthState.PortalHijack,
                $"探测返回异常状态码 {(int)resp.StatusCode}，请在浏览器手动认证");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CampusAuthStatus(CampusAuthState.NoInternet,
                ex is TaskCanceledException ? "探测超时（6秒无响应）" : "无法建立连接：" + ex.Message);
        }
    }

    /// <summary>从重定向 URL 特征识别门户厂商</summary>
    public static PortalKind DetectPortalKind(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("eportal") || lower.Contains("interface.do")) return PortalKind.RuijieEportal;
        if (lower.Contains("srun") || lower.Contains("srun_portal") || lower.Contains("a79.htm")) return PortalKind.Srun;
        if (lower.Contains("drcom") || lower.Contains("dr.com")) return PortalKind.DrCom;
        return PortalKind.Unknown;
    }

    /// <summary>从门户登录页地址解析出目录与 queryString</summary>
    public static (Uri PortalDir, string QueryString)? ParseRedirect(string? redirectUrl)
    {
        if (string.IsNullOrWhiteSpace(redirectUrl) ||
            !Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath;
        var dir = path.EndsWith('/') ? path : path[..(path.LastIndexOf('/') + 1)];
        var query = uri.Query.TrimStart('?');
        return (new Uri($"{uri.Scheme}://{uri.Authority}{dir}"), query);
    }

    public async Task<CampusLoginResult> LoginAsync(
        Uri portalDir, string queryString, string userId, string password, string service,
        PortalKind kind = PortalKind.RuijieEportal, CancellationToken ct = default)
    {
        try
        {
            return kind switch
            {
                PortalKind.RuijieEportal => await LoginRuijie(portalDir, queryString, userId, password, service, ct),
                PortalKind.Srun => await LoginSrun(portalDir, queryString, userId, password, service, ct),
                PortalKind.DrCom => await LoginDrCom(portalDir, queryString, userId, password, service, ct),
                _ => await LoginRuijie(portalDir, queryString, userId, password, service, ct), // 未知先试锐捷协议
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CampusLoginResult(false,
                ex is TaskCanceledException ? "登录请求超时" : "登录请求失败：" + ex.Message);
        }
    }

    /// <summary>锐捷 ePortal：POST InterFace.do?method=login</summary>
    private async Task<CampusLoginResult> LoginRuijie(Uri dir, string qs, string user, string pass, string service, CancellationToken ct)
    {
        var url = new Uri(dir, "InterFace.do?method=login");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userId"] = user, ["password"] = pass,
            ["service"] = string.IsNullOrEmpty(service) ? "internet" : service,
            ["queryString"] = qs, ["passwordEncrypt"] = "false",
        });
        using var resp = await _http.PostAsync(url, content, ct);
        return ParseLoginResponse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>深澜 Srun：POST srun_portal</summary>
    private async Task<CampusLoginResult> LoginSrun(Uri dir, string qs, string user, string pass, string service, CancellationToken ct)
    {
        var url = new Uri(dir, "srun_portal");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "login",
            ["username"] = user, ["password"] = pass,
            ["ac_id"] = "1",
            ["ip"] = "",
            ["queryString"] = qs,
            ["info"] = "{{\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}}",
            ["enc_ver"] = "srun_bx1",
        });
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        // 深澜返回 JSON 或 HTML
        if (body.Contains("login_ok") || body.Contains("\"code\":0") || body.Contains("success"))
            return new CampusLoginResult(true, "认证成功");
        return new CampusLoginResult(false, string.IsNullOrWhiteSpace(body) ? "空响应" : Truncate(body));
    }

    /// <summary>Dr.COM：POST login</summary>
    private async Task<CampusLoginResult> LoginDrCom(Uri dir, string qs, string user, string pass, string service, CancellationToken ct)
    {
        var url = new Uri(dir, "login");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DDDDD"] = user, ["upass"] = pass,
            ["R1"] = "0", ["R2"] = "0", ["R3"] = "0", ["R6"] = "0",
            ["0MKKey"] = "", ["buttonClicked"] = "", ["redirect_url"] = "",
            ["err_flag"] = "0", ["v6ip"] = "",
        });
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Contains("success") || resp.RequestMessage?.RequestUri?.ToString().Contains("success") == true)
            return new CampusLoginResult(true, "认证成功");
        return new CampusLoginResult(false, Truncate(body));
    }

    private static CampusLoginResult ParseLoginResponse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) &&
                string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                return new CampusLoginResult(true, "认证成功");

            string? message = null;
            if (root.TryGetProperty("message", out var m)) message = m.GetString();
            else if (root.TryGetProperty("msg", out var m2)) message = m2.GetString();
            return new CampusLoginResult(false,
                string.IsNullOrWhiteSpace(message) ? $"门户返回：{Truncate(text)}" : message);
        }
        catch (JsonException)
        {
            return new CampusLoginResult(false, $"门户响应格式异常：{Truncate(text)}");
        }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";

    public void Dispose() => _http.Dispose();
}
