using System.Runtime.InteropServices;
using WinDivertSharp;

// WinDivert 抓包层诊断探针 v3：
// Forward 层开三个对照句柄——引擎原版过滤器(含 IPv6 子句) / 纯 IPv4 版 / true，
// 一次运行分辨"引擎过滤器为何 0 包"。另保留 Network 层全量与 FLOW 层（带错误码）。
// 用法：交互模式直接运行；代理运行用 --auto <日志文件>。

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
P("=== WinDivert 抓包层诊断探针 v3 ===");
P($"抓取 {DurationSeconds} 秒。请确保手机连热点并高速跑流量，宿主机尽量静默。");
if (logPath is null)
{
    Console.WriteLine("按回车开始…");
    Console.ReadLine();
}

// 引擎 BuildFilter 的原版输出（v0.2.2/v0.3.x TrafficEngine 实际使用的字符串）
const string SubnetFull = "((ip.SrcAddr >= 192.168.137.0 and ip.SrcAddr <= 192.168.137.255) or " +
    "(ip.DstAddr >= 192.168.137.0 and ip.DstAddr <= 192.168.137.255)) or " +
    "(ipv6.SrcAddr >= fe80:: and ipv6.SrcAddr <= fe80::ffff:ffff:ffff:ffff) or " +
    "(ipv6.DstAddr >= fe80:: and ipv6.DstAddr <= fe80::ffff:ffff:ffff:ffff)";
// 纯 IPv4 版（去掉 IPv6 子句）
const string SubnetV4Only = "(ip.SrcAddr >= 192.168.137.0 and ip.SrcAddr <= 192.168.137.255) or " +
    "(ip.DstAddr >= 192.168.137.0 and ip.DstAddr <= 192.168.137.255)";

// 优先级：数字越小越先匹配。subnetFull(-200) → subnetV4Only(-100) → true(0)。
// 若 Full 工作则 Full>0 且 V4Only=0（被 Full 截流）；若 Full 失效则 V4Only>0；都失效则只剩 true。
var openings = new[]
{
    (name: "Forward/引擎原版(含v6子句)", filter: SubnetFull, priority: (short)(-200), stat: new LayerStat()),
    (name: "Forward/纯IPv4版", filter: SubnetV4Only, priority: (short)(-100), stat: new LayerStat()),
    (name: "Forward/true全量", filter: "true", priority: (short)0, stat: new LayerStat()),
    (name: "Network/true全量", filter: "true", priority: (short)0, stat: new LayerStat()),
};

foreach (var (name, filter, priority, stat) in openings)
{
    var handle = WinDivert.WinDivertOpen(filter, name.StartsWith("Network") ? WinDivertLayer.Network : WinDivertLayer.Forward, priority, WinDivertOpenFlags.None);
    if (handle == IntPtr.Zero)
    {
        var err = Marshal.GetLastWin32Error();
        P($"[{name}] 打开失败：Win32 {err}");
        continue;
    }
    var t = new Thread(() => Capture(handle, stat, DurationSeconds)) { Name = $"probe-{name}" };
    stat.Thread = t;
    t.Start();
}

var flowStat = new FlowStat();
var flowThread = new Thread(() =>
{
    var h = WinDivertNative.WDOpen("true", 1 /* FLOW */, 0, 0);
    if (h == IntPtr.Zero) { flowStat.OpenError = Marshal.GetLastWin32Error(); return; }
    var buf = new byte[0x10000];
    var addr = new byte[128];
    var deadline = DateTime.UtcNow.AddSeconds(DurationSeconds);
    while (DateTime.UtcNow < deadline)
    {
        uint len = 0;
        if (!WinDivertNative.WDRecv(h, buf, (uint)buf.Length, ref len, addr))
        {
            flowStat.RecvError = Marshal.GetLastWin32Error();
            break;
        }
        flowStat.Count(addr);
    }
    WinDivertNative.WDClose(h);
}) { Name = "probe-flow" };
flowThread.Start();

P($"抓包中… {DurationSeconds} 秒");
foreach (var s in openings) s.stat.Thread?.Join();
flowThread.Join();

P("");
P("===== FLOW 层（连接事件）=====");
if (flowStat.OpenError != 0) P($"打开失败：Win32 {flowStat.OpenError}");
else if (flowStat.RecvError != 0) P($"Recv 失败退出：Win32 {flowStat.RecvError}（0 事件不可信）");
else
{
    P($"事件总数 {flowStat.Total}");
    foreach (var line in flowStat.Samples.Take(30)) P("  " + line);
}
P("");

foreach (var (name, filter, priority, stat) in openings)
{
    P($"===== {name}（priority={priority}）=====");
    P($"总包数 {stat.Total}（IPv4 {stat.V4}）");
    if (name.Contains("true全量"))
    {
        P("TTL 分布：" + (stat.TtlHist.Count == 0 ? "无" : string.Join("  ", stat.TtlHist.OrderBy(x => x.Key).Select(x => $"TTL{x.Key}×{x.Value}"))));
        P("样本：");
        foreach (var line in stat.Samples.Take(30)) P("  " + line);
    }
    P("");
}

P("判定：引擎原版>0 → 引擎过滤器没问题（另找原因）；纯IPv4>0且原版=0 → IPv6 子句毒化过滤器，引擎需拆分；都=0 → 比较运算符失效，引擎改写过滤器。");

if (logPath is null)
{
    Console.WriteLine("\n按回车退出…");
    Console.ReadLine();
}
else P($"=== 完成，结果已写入 {logPath} ===");
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

internal static class WinDivertNative
{
    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertOpen")]
    public static extern IntPtr WDOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    // 原生签名：WinDivertRecv(HANDLE, VOID* pPacket, UINT packetLen, UINT* pRecvLen, WINDIVERT_ADDRESS* pAddr)
    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertRecv")]
    public static extern bool WDRecv(IntPtr handle, byte[] buffer, uint packetLen, ref uint length, [Out] byte[] addr);

    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertClose")]
    public static extern bool WDClose(IntPtr handle);
}

internal sealed class FlowStat
{
    public int OpenError;
    public int RecvError;
    public long Total;
    public readonly List<string> Samples = new();
    private readonly object _lock = new();

    public void Count(byte[] a)
    {
        lock (_lock)
        {
            Total++;
            if (Samples.Count >= 30) return;
            uint bits = BitConverter.ToUInt32(a, 8);
            if ((int)(bits & 0xFF) != 1) return;
            bool outbound = (bits & 0x100) != 0;
            const int ep = 12;
            int localOff = ep + 20;
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
        if (a[off + 10] == 0xff && a[off + 11] == 0xff)
            return $"{a[off + 12]}.{a[off + 13]}.{a[off + 14]}.{a[off + 15]}";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 16; i += 2)
            sb.Append(((a[off + i] << 8) | a[off + i + 1]).ToString("x")).Append(':');
        return sb.ToString(0, sb.Length - 1);
    }
}

sealed class LayerStat
{
    public Thread? Thread;
    public long Total;
    public long V4;
    public readonly Dictionary<int, int> TtlHist = new();
    public readonly List<string> Samples = new();
    private readonly object _lock = new();

    public void Count(WinDivertAddress addr, WinDivertBuffer buffer, uint len)
    {
        lock (_lock)
        {
            Total++;
            if (len < 20 || (buffer[0] >> 4) != 4) return;
            V4++;
            var src = $"{buffer[12]}.{buffer[13]}.{buffer[14]}.{buffer[15]}";
            var dst = $"{buffer[16]}.{buffer[17]}.{buffer[18]}.{buffer[19]}";
            var ttl = buffer[8];
            TtlHist[ttl] = TtlHist.TryGetValue(ttl, out var n) ? n + 1 : 1;
            if (Samples.Count < 30)
            {
                var proto = buffer[9] switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", _ => $"ip{buffer[9]}" };
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
