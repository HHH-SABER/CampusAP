using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampusAP.Core.Devices;

namespace CampusAP.Core.Web;

/// <summary>
/// 内置 Web 管理服务（参考 WiFi共享精灵 TX_Httpd.exe）。
/// 手机连热点后浏览器访问 http://热点网关:8899 即可查看设备列表与实时流量，无需装 App。
///
/// 安全模型：
/// - 热点本身是信任边界（能连上的都知道 WiFi 密码），但仍加了一层一次性令牌：
///   管理页 URL 带 ?token=，API 调用必须回传同一令牌。没有令牌的跨源请求（如手机浏览器里
///   恶意网页发出的 fire-and-forget POST）会被 403 挡下，防 CSRF。
/// - 设备名等动态数据在页内渲染前做 HTML 转义，防恶意主机名注入管理页。
/// - 只绑定热点网关地址与 localhost，不暴露到宿主机上行网卡。
/// </summary>
public sealed class WebConsoleServer : IDisposable
{
    private HttpListener? _listener;
    private Thread? _worker;
    private volatile bool _running;
    private string _token = "";

    /// <summary>热点网关 IP：ICS 经典默认 192.168.137.1；Win11 随机网段时由启动方传入实际值</summary>
    public string Gateway { get; private set; } = "192.168.137.1";
    public const int Port = 8899;

    /// <summary>获取当前设备列表的回调（由 UI 层注入）</summary>
    public Func<IReadOnlyList<DeviceInfo>>? DeviceProvider;

    public bool IsRunning => _running;

    public WebConsoleServer()
    {
    }

    /// <param name="gatewayIp">热点网关地址（取自当前客户端网段 .1；空则用 ICS 经典默认）</param>
    public void Start(string? gatewayIp = null)
    {
        if (_running) return;
        Gateway = string.IsNullOrWhiteSpace(gatewayIp) ? "192.168.137.1" : gatewayIp;
        _token = RandomNumberGenerator.GetHexString(16); // 一次性令牌，每次启动轮换
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{Gateway}:{Port}/");
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _running = true;
            _worker = new Thread(HandleLoop) { IsBackground = true, Name = "WebConsole" };
            _worker.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Web管理服务启动失败（端口 {Port} 可能被占用）：{ex.Message}", ex);
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _worker?.Join(1500); } catch { }
        _listener = null;
        _worker = null;
    }

    private void HandleLoop()
    {
        while (_running && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { if (_running) continue; break; }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path != "/" && !IsAuthorized(ctx))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    continue;
                }
                switch (path)
                {
                    case "/":
                        SendHtml(ctx, PageHtml.Replace("__TOKEN__", _token));
                        break;
                    case "/api/devices":
                        SendJson(ctx, GetDevicesJson());
                        break;
                    default:
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        break;
                }
            }
            catch { /* 单次请求异常不影响服务 */ }
        }
    }

    /// <summary>管理页以外的一切端点都要求令牌（查询串 ?token= 或 X-Web-Token 头）</summary>
    private bool IsAuthorized(HttpListenerContext ctx)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        var query = ctx.Request.QueryString["token"];
        var header = ctx.Request.Headers["X-Web-Token"];
        return FixedTimeEquals(query, _token) || FixedTimeEquals(header, _token);
    }

    private static bool FixedTimeEquals(string? a, string b)
    {
        if (string.IsNullOrEmpty(a) || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < b.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private string GetDevicesJson()
    {
        var devices = DeviceProvider?.Invoke() ?? new List<DeviceInfo>();
        var list = devices.Select(d => new
        {
            ip = d.Ip,
            mac = d.Mac,
            name = d.DisplayName,
            vendor = d.Vendor,
            upRate = d.UpRate,
            downRate = d.DownRate,
        });
        return JsonSerializer.Serialize(new { ok = true, devices = list });
    }

    private static void SendJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static void SendHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    public void Dispose() => Stop();

    /// <summary>设备信息快照（由 UI 层填充）</summary>
    public sealed record DeviceInfo(
        string Ip, string Mac, string DisplayName, string? Vendor,
        string UpRate, string DownRate);

    private const string PageHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>CampusAP 设备管理</title>
<style>
* { margin:0; padding:0; box-sizing:border-box; }
body { font-family: -apple-system, "Microsoft YaHei UI", sans-serif; background:#f1f5f9; padding:16px; }
h1 { font-size:20px; color:#0f172a; margin-bottom:14px; }
.card { background:#fff; border-radius:12px; padding:14px; margin-bottom:10px; box-shadow:0 1px 3px rgba(0,0,0,.08); }
.dev-name { font-size:16px; font-weight:600; color:#0f172a; }
.dev-meta { font-size:12px; color:#64748b; margin-top:2px; }
.dev-rates { font-size:13px; color:#2563eb; margin-top:6px; }
.row { display:flex; justify-content:space-between; align-items:center; }
</style>
</head>
<body>
<h1>CampusAP 设备管理</h1>
<div id="devices">加载中…</div>
<script>
const AUTH = '__TOKEN__';
// HTML 转义：设备名/主机名来自局域网，不可信，渲染前必须转义
function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({
    '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'
  }[c]));
}
async function load() {
  try {
    const r = await fetch('/api/devices?token=' + encodeURIComponent(AUTH));
    const d = await r.json();
    const el = document.getElementById('devices');
    if (!d.devices.length) { el.innerHTML = '<div class="card">暂无设备连接</div>'; return; }
    el.innerHTML = d.devices.map((dev,i) => `
      <div class="card">
        <div class="row">
          <div>
            <div class="dev-name">${esc(dev.name || dev.ip)}</div>
            <div class="dev-meta">${esc(dev.vendor ? dev.vendor + ' · ' : '')}${esc(dev.mac)} · ${esc(dev.ip)}</div>
            <div class="dev-rates">↑ ${esc(dev.upRate)} · ↓ ${esc(dev.downRate)}</div>
          </div>
        </div>
      </div>`).join('');
  } catch(e) {
    document.getElementById('devices').innerHTML = '<div class="card">连接失败：' + esc(e.message) + '</div>';
  }
}
load();
setInterval(load, 3000);
</script>
</body>
</html>
""";
}
