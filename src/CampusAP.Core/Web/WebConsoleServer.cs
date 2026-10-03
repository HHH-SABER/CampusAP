using System.Net;
using System.Text;
using System.Text.Json;
using CampusAP.Core.Devices;

namespace CampusAP.Core.Web;

/// <summary>
/// 内置 Web 管理服务（参考 WiFi共享精灵 TX_Httpd.exe）。
/// 手机连热点后浏览器访问 http://192.168.137.1:8899 即可看设备列表、限速、拉黑，无需装 App。
/// </summary>
public sealed class WebConsoleServer : IDisposable
{
    private readonly TrafficEngine _engine;
    private HttpListener? _listener;
    private Thread? _worker;
    private volatile bool _running;

    /// <summary>热点网关地址：ICS 默认 192.168.137.1</summary>
    public const string GatewayIp = "192.168.137.1";
    public const int Port = 8899;

    /// <summary>获取当前设备列表的回调（由 UI 层注入）</summary>
    public Func<IReadOnlyList<DeviceInfo>>? DeviceProvider;

    /// <summary>限速命令回调（IP, 字节/秒）</summary>
    public Action<string, long>? LimitCommand;

    /// <summary>拉黑命令回调（IP, 是否拉黑）</summary>
    public Action<string, bool>? BlockCommand;

    public bool IsRunning => _running;

    public WebConsoleServer(TrafficEngine engine)
    {
        _engine = engine;
    }

    public void Start()
    {
        if (_running) return;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{GatewayIp}:{Port}/");
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
                switch (path)
                {
                    case "/":
                        SendHtml(ctx, PageHtml);
                        break;
                    case "/api/devices":
                        SendJson(ctx, GetDevicesJson());
                        break;
                    case "/api/limit":
                        HandleLimit(ctx);
                        break;
                    case "/api/block":
                        HandleBlock(ctx);
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
            blocked = d.Blocked,
            limit = d.LimitBytesPerSec,
        });
        return JsonSerializer.Serialize(new { ok = true, devices = list });
    }

    private void HandleLimit(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = reader.ReadToEnd();
        try
        {
            var doc = JsonDocument.Parse(body);
            var ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
            var bytesPerSec = doc.RootElement.GetProperty("bytesPerSec").GetInt64();
            LimitCommand?.Invoke(ip, bytesPerSec);
            SendJson(ctx, "{\"ok\":true}");
        }
        catch
        {
            SendJson(ctx, "{\"ok\":false,\"error\":\"bad request\"}");
        }
    }

    private void HandleBlock(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = reader.ReadToEnd();
        try
        {
            var doc = JsonDocument.Parse(body);
            var ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
            var blocked = doc.RootElement.GetProperty("blocked").GetBoolean();
            BlockCommand?.Invoke(ip, blocked);
            SendJson(ctx, "{\"ok\":true}");
        }
        catch
        {
            SendJson(ctx, "{\"ok\":false,\"error\":\"bad request\"}");
        }
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
        string UpRate, string DownRate, bool Blocked, long LimitBytesPerSec);

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
.btns { margin-top:10px; display:flex; gap:8px; flex-wrap:wrap; }
button { padding:6px 14px; border:none; border-radius:8px; font-size:13px; cursor:pointer; }
.btn-block { background:#ef4444; color:#fff; }
.btn-unblock { background:#22c55e; color:#fff; }
select { padding:6px; border:1px solid #cbd5e1; border-radius:8px; font-size:13px; }
.tag { font-size:11px; padding:2px 8px; border-radius:10px; margin-left:6px; }
.tag-blocked { background:#fef2f2; color:#ef4444; }
</style>
</head>
<body>
<h1>CampusAP 设备管理</h1>
<div id="devices">加载中…</div>
<script>
async function load() {
  try {
    const r = await fetch('/api/devices');
    const d = await r.json();
    const el = document.getElementById('devices');
    if (!d.devices.length) { el.innerHTML = '<div class="card">暂无设备连接</div>'; return; }
    el.innerHTML = d.devices.map((dev,i) => `
      <div class="card">
        <div class="row">
          <div>
            <div class="dev-name">${dev.name || dev.ip}${dev.blocked ? '<span class="tag tag-blocked">已拉黑</span>' : ''}</div>
            <div class="dev-meta">${dev.vendor ? dev.vendor + ' · ' : ''}${dev.mac} · ${dev.ip}</div>
            <div class="dev-rates">↑ ${dev.upRate} · ↓ ${dev.downRate}</div>
          </div>
        </div>
        <div class="btns">
          <select onchange="setLimit('${dev.ip}', this.value)">
            <option value="0">不限速</option>
            <option value="2500000" ${dev.limit===2500000?'selected':''}>20 Mbps</option>
            <option value="625000" ${dev.limit===625000?'selected':''}>5 Mbps</option>
            <option value="125000" ${dev.limit===125000?'selected':''}>1 Mbps</option>
            <option value="32000" ${dev.limit===32000?'selected':''}>256 Kbps</option>
          </select>
          <button class="${dev.blocked?'btn-unblock':'btn-block'}" onclick="toggleBlock('${dev.ip}', ${!dev.blocked})">
            ${dev.blocked ? '解除拉黑' : '拉黑'}
          </button>
        </div>
      </div>`).join('');
  } catch(e) {
    document.getElementById('devices').innerHTML = '<div class="card">连接失败：' + e.message + '</div>';
  }
}
async function setLimit(ip, bytes) {
  await fetch('/api/limit', {method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ip, bytesPerSec: parseInt(bytes)})});
  load();
}
async function toggleBlock(ip, blocked) {
  await fetch('/api/block', {method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ip, blocked})});
  load();
}
load();
setInterval(load, 3000);
</script>
</body>
</html>
""";
}
