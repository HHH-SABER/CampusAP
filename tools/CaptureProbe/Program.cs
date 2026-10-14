using System.Runtime.InteropServices;
using WinDivertSharp;

// WinDivert 抓包层诊断探针 v3：
// Forward 层开三个对照句柄——引擎原版过滤器(含 IPv6 子句) / 纯 IPv4 版 / true，
// 一次运行分辨"引擎过滤器为何 0 包"。另保留 Network 层全量与 FLOW 层（带错误码）。
// 用法：交互模式直接运行；代理运行用 --auto <日志文件>。

const int DurationSeconds = 30;

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
    var h = WinDivertNative.WDOpen("true", 2 /* FLOW：WinDivert 2.x 层值 NETWORK=0/FORWARD=1/FLOW=2；WinDivertSharp 枚举未封装 Flow */, 0, 0);
    if (h == IntPtr.Zero) { flowStat.OpenError = Marshal.GetLastWin32Error(); return; }
    var buf = new byte[0x10000];
    var addr = new byte[1024]; // WINDIVERT_ADDRESS 完整结构含 Reflect union（x64 约 320+ 字节），必须给足否则溢出
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
        // Flow 层语义：Recv 后必须 Send 回注 = permit 该流；不 Send = block（会断流！）
        uint sent = 0;
        WinDivertNative.WDSend(h, buf, 0, ref sent, addr);
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
    P($"事件总数 {flowStat.Total}（建立 {flowStat.Established} / 删除 {flowStat.Deleted}，回环 {flowStat.Loopback}）");
    foreach (var line in flowStat.Samples.Take(40)) P("  " + line);
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

    // 原生签名：WinDivertSend(HANDLE, VOID* pPacket, UINT packetLen, UINT* pSendLen, WINDIVERT_ADDRESS* pAddr)
    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertSend")]
    public static extern bool WDSend(IntPtr handle, byte[] buffer, uint packetLen, ref uint length, byte[] addr);

    [DllImport("WinDivert.dll", SetLastError = true, EntryPoint = "WinDivertClose")]
    public static extern bool WDClose(IntPtr handle);
}

internal sealed class FlowStat
{
    public int OpenError;
    public int RecvError;
    public long Total;
    public long Established;
    public long Deleted;
    public long Loopback;
    public readonly List<string> Samples = new();
    private readonly object _lock = new();

    // WINDIVERT_ADDRESS（x64）偏移：0 Timestamp(8) | 8 位域(Layer:8,Event:8,Loopback:1@16,Outbound:1@17,IPv6:1@19)
    // | 12 Reserved2(2) + pad(2) | 16 Flow union: Endpoint(8) ParentEndpoint(8) ProcessId(4)@32
    // LocalAddr[4]@36 RemoteAddr[4]@52 LocalPort@68 RemotePort@70 Protocol@72
    public void Count(byte[] a)
    {
        lock (_lock)
        {
            var layer = a[8];
            if (layer != 1) return; // 仅统计 FLOW 层事件
            Total++;
            var flags = BitConverter.ToUInt32(a, 8);
            var evt = (int)(flags >> 8 & 0xFF);
            if (evt == 1) Established++; else if (evt == 2) Deleted++;
            var isV6 = (flags & 0x80000) != 0;          // bit19
            var isLoopback = (flags & 0x10000) != 0;    // bit16
            var outbound = (flags & 0x20000) != 0;      // bit17
            if (isLoopback) Loopback++;

            var pid = BitConverter.ToUInt32(a, 32);
            var proto = a[72] switch { 6 => "TCP", 17 => "UDP", _ => $"p{a[72]}" };
            var local = Addr(a, 36, isV6);
            var remote = Addr(a, 52, isV6);
            var lport = BitConverter.ToUInt16(a, 68);
            var rport = BitConverter.ToUInt16(a, 70);

            var evtName = evt switch { 1 => "建", 2 => "删", _ => $"e{evt}" };
            var line = $"{evtName} {(outbound ? "出" : "入")} {(isV6 ? "v6" : "v4")} {proto} {local}:{lport} → {remote}:{rport} PID={pid}{(isLoopback ? " [回环]" : "")}";
            if (Samples.Count < 20)
            {
                Samples.Add(line);
                if (Samples.Count == 1)
                    Samples.Add("HEX[0..79]: " + Convert.ToHexString(a, 0, 80));
            }
            else if (Samples.Count < 40 && evt == 1 && !isLoopback && Samples.Count(s => s.Contains("建")) < 40)
            {
                Samples.Add(line);
            }
        }
    }

    private static string Addr(byte[] a, int off, bool v6)
    {
        if (!v6 && a[off + 10] == 0xff && a[off + 11] == 0xff)
            return $"{a[off + 12]}.{a[off + 13]}.{a[off + 14]}.{a[off + 15]}";
        if (!v6)
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
