using System.Runtime.InteropServices;
using WinDivertSharp;

// WinDivert 抓包层诊断探针：
// Network 层（to/from local machine）+ Forward 层（passing through）+ FLOW 层（连接事件），
// 各抓 12 秒。用于判断本机热点 NAT 的转发路径：
//   · Forward/Network 是否可见转发流量；
//   · FLOW 事件的本地地址是 NAT 前（192.168.137.x，可按设备映射）还是 NAT 后。
// 用法：交互模式直接运行；代理运行用 --auto <日志文件>（免交互、输出落文件）。

const int DurationSeconds = 12;

string? logPath = args.FirstOrDefault() == "--auto" ? args.ElementAtOrDefault(1) : null;

void P(string s)
{
    Console.WriteLine(s);
    if (logPath is not null)
        File.AppendAllText(logPath, s + Environment.NewLine, System.Text.Encoding.UTF8);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (logPath is not null && File.Exists(logPath)) File.Delete(logPath);
P("=== WinDivert 抓包层诊断探针 ===");
P($"将全量抓取 {DurationSeconds} 秒，期间网络经本程序中转，属正常现象。");
if (logPath is null)
{
    Console.WriteLine("请确保：手机已连热点，且正在跑流量（测速/视频）。按回车开始…");
    Console.ReadLine();
}
else
{
    P($"免交互模式，结果将写入 {logPath}");
}

var layers = new Dictionary<WinDivertLayer, LayerStat>
{
    [WinDivertLayer.Network] = new(),
    [WinDivertLayer.Forward] = new(),
};

foreach (var layer in layers.Keys.ToList())
{
    var handle = WinDivert.WinDivertOpen("true", layer, 0, WinDivertOpenFlags.None);
    if (handle == IntPtr.Zero)
    {
        var err = Marshal.GetLastWin32Error();
        P($"[{layer}] 打开失败：Win32 错误码 {err}（5=需要管理员，2=缺 WinDivert 文件）");
        layers.Remove(layer);
        continue;
    }
    var stat = layers[layer];
    var thread = new Thread(() => Capture(handle, stat, DurationSeconds)) { Name = $"probe-{layer}" };
    stat.Thread = thread;
    thread.Start();
}

// FLOW 层：连接建立事件。winnat 连接重定向型 NAT 下 Forward 层 0 包，
// 但 ALE 流事件可能携带 NAT 前的本地地址——若命中 192.168.137.x 即拿到 设备<->NAT端口 映射。
// WinDivertSharp 的地址结构不含 FLOW 数据区，这里自写 P/Invoke 读完整 WINDIVERT_ADDRESS。
var flowStat = new FlowStat();
var flowThread = new Thread(() =>
{
    var h = WinDivertNative.WDOpen("true", 1 /* FLOW */, 0, 0);
    if (h == IntPtr.Zero) { flowStat.OpenError = Marshal.GetLastWin32Error(); return; }
    var buf = new byte[0x10000];
    var addr = new byte[128]; // 原生驱动按完整结构写入，缓冲区必须足够
    var deadline = DateTime.UtcNow.AddSeconds(DurationSeconds);
    while (DateTime.UtcNow < deadline)
    {
        uint len = 0;
        if (!WinDivertNative.WDRecv(h, buf, ref len, addr)) break;
        flowStat.Count(addr);
    }
    WinDivertNative.WDClose(h);
}) { Name = "probe-flow" };
flowThread.Start();

P($"抓包中… {DurationSeconds} 秒");
foreach (var stat in layers.Values) stat.Thread?.Join();
flowThread.Join();

P("");
P("===== FLOW 层（连接事件）=====");
if (flowStat.OpenError != 0)
    P($"打开失败：Win32 {flowStat.OpenError}（5=需要管理员）");
else
{
    P($"事件总数 {flowStat.Total}（含本机自身连接，样本限量 40 条）");
    P("样本（方向 协议 local:port → remote:port PID）——重点看有没有 192.168.137.x：");
    foreach (var line in flowStat.Samples.Take(40)) P("  " + line);
}
P("");

foreach (var kv in layers)
{
    var s = kv.Value;
    P($"===== {kv.Key} 层 =====");
    P($"总包数 {s.Total}（IPv4 {s.V4}，IPv6/其他 {s.Total - s.V4}）");
    P($"方向：outbound={s.Outbound}  inbound={s.Inbound}");
    P("TTL 分布（IPv4 样本）：" +
        (s.TtlHist.Count == 0 ? "无" : string.Join("  ", s.TtlHist.OrderBy(x => x.Key).Select(x => $"TTL{x.Key}×{x.Value}"))));
    P("样本（src:port → dst:port 协议 TTL）：");
    foreach (var line in s.Samples.Take(40)) P("  " + line);
    P("");
}

P("解读提示：");
P("· Forward 层有 192.168.137.x 源地址的包 → 可在 Forward 层按设备统计与管控（v0.2.2 设计预期）；");
P("· Forward/Network 都 0 包但 FLOW 事件带 137.x 本地地址 → 用 FLOW 事件做 设备<->NAT端口 映射 + Network 层管控；");
P("· FLOW 事件也全是出口 IP → winnat 完全封闭，转 Npcap/ETW NDIS 旁路统计方案。");

if (logPath is null)
{
    Console.WriteLine("\n按回车退出…");
    Console.ReadLine();
}
else
{
    P($"=== 探针完成，结果已写入 {logPath} ===");
}
Environment.Exit(0);

static void Capture(IntPtr handle, LayerStat stat, int seconds)
{
    var buffer = new WinDivertBuffer();
    var addr = new WinDivertAddress();
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        addr.Reset();
        uint len = 0;
        if (!WinDivert.WinDivertRecv(handle, buffer, ref addr, ref len)) break;
        stat.Count(addr, buffer, len);
        WinDivert.WinDivertSend(handle, buffer, len, ref addr);
    }
    WinDivert.WinDivertClose(handle);
}

// ---- FLOW 层：完整 WINDIVERT_ADDRESS 的手动解析 ----
// 结构：Timestamp(8) + 位域(4) + union{ Endpoint(8) ParentEndpoint(8) ProcessId(4) LocalAddr(16) RemoteAddr(16) LocalPort(4) RemotePort(4) Protocol(1)… }
internal sealed class FlowStat
{
    public int OpenError;
    public long Total;
    public readonly List<string> Samples = new();
    private readonly object _lock = new();

    public void Count(byte[] a)
    {
        lock (_lock)
        {
            Total++;
            if (Samples.Count >= 40) return; // 限样本量，防本机连接刷屏
            uint bits = BitConverter.ToUInt32(a, 8);
            if ((int)(bits & 0xFF) != 1) return; // 只认 FLOW 层
            bool outbound = (bits & 0x100) != 0;
            const int ep = 12; // union 起点
            int localOff = ep + 20; // Endpoint(8)+ParentEndpoint(8)+ProcessId(4)
            var local = Addr16(a, localOff);
            var remote = Addr16(a, localOff + 16);
            uint lport = BitConverter.ToUInt32(a, localOff + 32) & 0xFFFF;
            uint rport = BitConverter.ToUInt32(a, localOff + 36) & 0xFFFF;
            var proto = a[localOff + 40] switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", _ => $"p{a[localOff + 40]}" };
            Samples.Add($"{(outbound ? "出" : "入")} {proto} {local}:{lport} → {remote}:{rport} PID={BitConverter.ToUInt32(a, ep + 16)}");
        }
    }

    private static string Addr16(byte[] a, int off)
    {
        // IPv4 以 ::ffff:x.y.z.w 映射形式出现，读末 4 字节即可
        if (a[off + 10] == 0xff && a[off + 11] == 0xff)
            return $"{a[off + 12]}.{a[off + 13]}.{a[off + 14]}.{a[off + 15]}";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 16; i += 2)
            sb.Append(((a[off + i] << 8) | a[off + i + 1]).ToString("x")).Append(':');
        return sb.ToString(0, sb.Length - 1);
    }
}

internal static class WinDivertNative
{
    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertOpen")]
    public static extern IntPtr WDOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertRecv")]
    public static extern bool WDRecv(IntPtr handle, byte[] buffer, ref uint length, [Out] byte[] addr);

    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertClose")]
    public static extern bool WDClose(IntPtr handle);
}

sealed class LayerStat
{
    public Thread? Thread;
    public long Total;
    public long V4;
    public long Outbound;
    public long Inbound;
    public readonly Dictionary<int, int> TtlHist = new();
    public readonly List<string> Samples = new();
    private readonly object _lock = new();

    public void Count(WinDivertAddress addr, WinDivertBuffer buffer, uint len)
    {
        lock (_lock)
        {
            Total++;
            if (addr.Direction == WinDivertDirection.Outbound) Outbound++; else Inbound++;
            if (len < 20 || (buffer[0] >> 4) != 4) return;
            V4++;
            var src = $"{buffer[12]}.{buffer[13]}.{buffer[14]}.{buffer[15]}";
            var dst = $"{buffer[16]}.{buffer[17]}.{buffer[18]}.{buffer[19]}";
            var proto = buffer[9] switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", _ => $"ip{buffer[9]}" };
            var ttl = buffer[8];
            TtlHist[ttl] = TtlHist.TryGetValue(ttl, out var n) ? n + 1 : 1;
            if (Samples.Count < 40)
            {
                var ports = "";
                if (buffer[9] is 6 or 17)
                {
                    var ihl = (buffer[0] & 0x0F) * 4;
                    if (len >= ihl + 4)
                        ports = $":{(buffer[ihl] << 8) | buffer[ihl + 1]} → :{(buffer[ihl + 2] << 8) | buffer[ihl + 3]}";
                }
                Samples.Add($"{(addr.Direction == WinDivertDirection.Outbound ? "出" : "入")} {src}{ports} → {dst}  {proto} TTL={ttl}");
            }
        }
    }
}
