using System.Net.Http;
using System.Text.Json;

namespace CampusAP.Core.CampusAuth;

public enum CampusAuthState
{
    /// <summary>尚未检测</summary>
    Unknown,
    /// <summary>检测中</summary>
    Checking,
    /// <summary>已认证，直连正常</summary>
    Online,
    /// <summary>被门户 302 拦截（可自动登录，RedirectUrl 可用）</summary>
    NeedLogin,
    /// <summary>被网关劫持但拿不到可解析的跳转地址</summary>
    PortalHijack,
    /// <summary>无互联网通路</summary>
    NoInternet,
}

/// <summary>认证探测结果；NeedLogin 时 RedirectUrl 携带门户登录页地址（含加密 queryString）</summary>
public record CampusAuthStatus(CampusAuthState State, string Detail, string? RedirectUrl = null);

public record CampusLoginResult(bool Success, string? Message);

/// <summary>
/// 锐捷 ePortal 网页认证客户端（直发 POST 路线，协议参考开源 RuijieWIFI-AutoLogin）：
/// 1. 对中性 URL 发请求且不跟随跳转：200 且内容匹配 = 已认证；302 = 未认证，Location 即门户登录页；
/// 2. 取登录页 "?" 后整段加密 queryString，连同账密 POST 到同目录 InterFace.do?method=login；
/// 3. 响应 JSON result=="success" 即成功。
/// 门户地址不写死在任何文件里，完全由网关重定向动态获得；仅提交用户本人的账密。
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
            AllowAutoRedirect = false, // 302 的 Location 是获取门户地址与 queryString 的唯一来源
            UseProxy = false,
        })
        { Timeout = TimeSpan.FromSeconds(6) };
    }

    /// <summary>探测认证状态：正常 200 = 在线；被 302 = 未认证；200 但内容被换 = 劫持</summary>
    public async Task<CampusAuthStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(CheckUrl, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return body.Contains(CheckBodyMarker, StringComparison.OrdinalIgnoreCase)
                    ? new CampusAuthStatus(CampusAuthState.Online, "校园网认证有效，网络连通正常")
                    : new CampusAuthStatus(CampusAuthState.PortalHijack,
                        "HTTP 请求被网关替换内容（无跳转地址可解析），请在浏览器里手动完成认证");
            }

            if (resp.Headers.Location is not null)
            {
                var redirect = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(CheckUrl), resp.Headers.Location);
                return new CampusAuthStatus(CampusAuthState.NeedLogin,
                    $"校园网未认证（被门户 {redirect.Authority} 拦截）", redirect.ToString());
            }

            return new CampusAuthStatus(CampusAuthState.PortalHijack,
                $"探测返回异常状态码 {(int)resp.StatusCode}，请在浏览器里手动完成认证");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CampusAuthStatus(CampusAuthState.NoInternet,
                ex is TaskCanceledException ? "探测超时（6 秒无响应），网络可能不通" : "无法建立连接：" + ex.Message);
        }
    }

    /// <summary>从门户登录页地址解析出 InterFace.do 所在目录与整段 queryString</summary>
    public static (Uri PortalDir, string QueryString)? ParseRedirect(string? redirectUrl)
    {
        if (string.IsNullOrWhiteSpace(redirectUrl) ||
            !Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri) ||
            uri.Query.Length <= 1)
            return null;

        var path = uri.AbsolutePath;
        var dir = path.EndsWith('/') ? path : path[..(path.LastIndexOf('/') + 1)];
        return (new Uri($"{uri.Scheme}://{uri.Authority}{dir}"), uri.Query.TrimStart('?'));
    }

    public async Task<CampusLoginResult> LoginAsync(
        Uri portalDir, string queryString, string userId, string password, string service,
        CancellationToken ct = default)
    {
        var url = new Uri(portalDir, "InterFace.do?method=login");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userId"] = userId,
            ["password"] = password,
            ["service"] = service,
            ["queryString"] = queryString,
            ["passwordEncrypt"] = "false",
        });
        try
        {
            using var resp = await _http.PostAsync(url, content, ct);
            return ParseLoginResponse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CampusLoginResult(false,
                ex is TaskCanceledException ? "登录请求超时" : "登录请求失败：" + ex.Message);
        }
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
                string.IsNullOrWhiteSpace(message) ? $"门户返回失败：{Truncate(text)}" : message);
        }
        catch (JsonException)
        {
            return new CampusLoginResult(false, $"门户响应不是预期格式：{Truncate(text)}");
        }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";

    public void Dispose() => _http.Dispose();
}
