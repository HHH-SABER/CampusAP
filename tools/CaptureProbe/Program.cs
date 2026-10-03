using System.Runtime.InteropServices;
using WinDivertSharp;

// WinDivert 抓包层诊断探针：
// Network 层（to/from local machine）与 Forward 层（passing through）全量抓 12 秒，
// 输出每层的包数/方向/TTL 分布/五元组样本，用于判断本机热点 NAT 的转发路径
// 到底经过哪一层、地址是否已被转换（决定限速/流量统计的最终技术方案）。
// 用法：右键管理员运行 tools/CaptureProbe/run_probe.bat；抓包期间手机保持高速流量。

const int DurationSeconds = 12;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== WinDivert 抓包层诊断探针 ===");
Console.WriteLine($"将全量抓取 {DurationSeconds} 秒（filter=true，两层），期间网络经本程序中转，属正常现象。");
Console.WriteLine("请确保：手机已连热点，且正在跑流量（测速/视频）。按回车开始…");
Console.ReadLine();

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
        Console.WriteLine($"[{layer}] 打开失败：Win32 错误码 {err}（5=需要管理员，2=缺 WinDivert 文件）");
        layers.Remove(layer);
        continue;
    }
    var stat = layers[layer];
    var thread = new Thread(() => Capture(handle, stat, DurationSeconds)) { Name = $"probe-{layer}" };
    stat.Thread = thread;
    thread.Start();
}

Console.WriteLine($"抓包中… {DurationSeconds} 秒");
foreach (var stat in layers.Values) stat.Thread?.Join();

Console.WriteLine();
foreach (var kv in layers)
{
    var s = kv.Value;
    Console.WriteLine($"===== {kv.Key} 层 =====");
    Console.WriteLine($"总包数 {s.Total}（IPv4 {s.V4}，IPv6/其他 {s.Total - s.V4}）");
    Console.WriteLine($"方向：outbound={s.Outbound}  inbound={s.Inbound}");
    Console.WriteLine("TTL 分布（IPv4 出向样本）：" +
        (s.TtlHist.Count == 0 ? "无" : string.Join("  ", s.TtlHist.OrderBy(x => x.Key).Select(x => $"TTL{x.Key}×{x.Value}"))));
    Console.WriteLine("样本（src:port → dst:port 协议 TTL）：");
    foreach (var line in s.Samples.Take(40)) Console.WriteLine("  " + line);
    Console.WriteLine();
}

Console.WriteLine("解读提示：");
Console.WriteLine("· 若 Forward 层 0 包：转发流量不经过 IPFORWARD（winnat 连接重定向型 NAT），WinDivert 无法按设备管控；");
Console.WriteLine("· 若 Forward 层有包但地址全为出口 IP：NAT 在 IPFORWARD 前完成，同样无法按设备归属；");
Console.WriteLine("· 若 Forward 层有 192.168.137.x 源地址的包：可在 Forward 层做按设备统计与管控（回到 v0.2.2 设计预期）。");
Console.WriteLine("\n按回车退出（关闭前会自动 Ctrl-C 线程）…");
Console.ReadLine();
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
