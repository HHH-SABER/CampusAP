using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinDivertSharp;

namespace CampusAP.Core.Devices;

/// <summary>
/// 基于 WinDivert（用户态包过滤）的每设备流量引擎：
/// 按设备计数上下行、令牌桶限速（超额丢包，由 TCP 拥塞控制自然回压）、整包丢弃实现拉黑。
/// 需要管理员权限（加载 WinDivert 内核驱动）。
/// </summary>
public sealed class TrafficEngine : IDisposable
{
    /// <summary>Win10 移动热点私有网段（ICS 默认 192.168.137.0/24）</summary>
    private const string SubnetFilter =
        "(ip.SrcAddr >= 192.168.137.0 and ip.SrcAddr <= 192.168.137.255) or " +
        "(ip.DstAddr >= 192.168.137.0 and ip.DstAddr <= 192.168.137.255)";

    /// <summary>目标 TTL：手机包经 NAT 后减1，设为 129 让网关收到 127（与 Windows 直发一致）</summary>
    private const int UpstreamTtl = 129;

    private IntPtr _handle = IntPtr.Zero;
    private Thread? _worker;
    private volatile bool _running;
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();

    /// <summary>是否对热点上行包做 TTL 伪装（抹掉多设备指纹）</summary>
    public bool TtlSpoofEnabled { get; set; } = true;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _handle = WinDivert.WinDivertOpen(SubnetFilter, WinDivertLayer.Network, 0, WinDivertOpenFlags.None);
        if (_handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(err switch
            {
                5 => "流量管控需要管理员权限（WinDivert 驱动加载被拒绝）。",
                2 => "未找到 WinDivert 驱动文件：WinDivert.dll 和 WinDivert64.sys 需与主程序同目录。",
                577 or 1274 => "WinDivert 驱动加载被安全策略/杀毒软件阻止。",
                _ => $"WinDivert 打开失败（Win32 错误码 {err}）。",
            });
        }
        _running = true;
        _worker = new Thread(PacketLoop) { IsBackground = true, Name = "TrafficEngine" };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_handle != IntPtr.Zero)
        {
            WinDivert.WinDivertClose(_handle);
            _handle = IntPtr.Zero;
        }
        try { _worker?.Join(1500); } catch { }
        _worker = null;
    }

    /// <summary>同步受管设备集合（由 UI 轮询线程调用，传入系统 tethering API 的客户端 IP）</summary>
    public void SetClients(IEnumerable<string> ips)
    {
        var set = new HashSet<string>(ips);
        foreach (var ip in set) _devices.TryAdd(ip, new DeviceState());
        foreach (var kv in _devices)
            if (!set.Contains(kv.Key)) _devices.TryRemove(kv.Key, out _);
    }

    /// <summary>设置限速（字节/秒，0=不限）。即时生效。</summary>
    public void SetLimit(string ip, long bytesPerSec)
    {
        if (_devices.TryGetValue(ip, out var s))
            lock (s.TokenLock)
            {
                s.LimitBytesPerSec = bytesPerSec;
                s.Tokens = Math.Min(s.Tokens, bytesPerSec);
            }
    }

    /// <summary>设置拉黑状态。即时生效（双向丢包）。</summary>
    public void SetBlocked(string ip, bool blocked)
    {
        if (_devices.TryGetValue(ip, out var s)) s.Blocked = blocked;
    }

    /// <summary>读某设备累计上行/下行字节数</summary>
    public (long Up, long Down) GetTotals(string ip)
    {
        return _devices.TryGetValue(ip, out var s)
            ? (Interlocked.Read(ref s.TotalUp), Interlocked.Read(ref s.TotalDown))
            : (0, 0);
    }

    public void Dispose() => Stop();

    // ---- 抓包线程 ----

    private void PacketLoop()
    {
        var buffer = new WinDivertBuffer();
        var addr = new WinDivertAddress();
        while (_running)
        {
            addr.Reset();
            uint len = 0;
            if (!WinDivert.WinDivertRecv(_handle, buffer, ref addr, ref len))
            {
                if (!_running) break;
                continue;
            }
            if (len < 20 || (buffer[0] >> 4) != 4)
            {
                Reinject(buffer, len, ref addr);
                continue;
            }

            var src = $"{buffer[12]}.{buffer[13]}.{buffer[14]}.{buffer[15]}";
            var dst = $"{buffer[16]}.{buffer[17]}.{buffer[18]}.{buffer[19]}";

            bool isClient = _devices.TryGetValue(src, out var state);
            if (!isClient)
            {
                isClient = _devices.TryGetValue(dst, out state);
            }
            if (!isClient)
            {
                Reinject(buffer, len, ref addr); // 非受管流量直通
                continue;
            }

            if (_devices.TryGetValue(src, out var up))
                Interlocked.Add(ref up.TotalUp, len);
            if (_devices.TryGetValue(dst, out var down))
                Interlocked.Add(ref down.TotalDown, len);

            if (state!.Blocked) continue;                    // 拉黑：双向丢弃
            if (state.LimitBytesPerSec > 0 && !TryConsumeToken(state, len))
                continue;                                    // 限速：超额丢弃

            // TTL 伪装：客户端上行包（src=热点IP）改成 129，NAT 后与 Windows 直发一致
            if (TtlSpoofEnabled && _devices.ContainsKey(src) && buffer[8] != UpstreamTtl)
            {
                buffer[8] = UpstreamTtl;
                FixIpChecksum(buffer, len);
            }

            Reinject(buffer, len, ref addr);
        }
    }

    private void Reinject(WinDivertBuffer buffer, uint len, ref WinDivertAddress addr)
        => WinDivert.WinDivertSend(_handle, buffer, len, ref addr);

    /// <summary>重算 IPv4 头校验和（改了 TTL 后必须调用，否则包被丢弃）</summary>
    private static void FixIpChecksum(WinDivertBuffer buffer, uint len)
    {
        int ihl = (buffer[0] & 0x0F) * 4;   // IP 头长度
        if (len < ihl || ihl < 20) return;

        // 清零校验和字段（offset 12-13）
        buffer[12] = 0;
        buffer[13] = 0;

        uint sum = 0;
        for (int i = 0; i < ihl; i += 2)
        {
            ushort word = (ushort)((buffer[i] << 8) | buffer[i + 1]);
            sum += word;
        }
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        ushort checksum = (ushort)(~sum);
        buffer[12] = (byte)(checksum >> 8);
        buffer[13] = (byte)(checksum & 0xFF);
    }

    private static bool TryConsumeToken(DeviceState s, uint cost)
    {
        lock (s.TokenLock)
        {
            var now = DateTime.UtcNow;
            s.Tokens = Math.Min(s.LimitBytesPerSec,
                s.Tokens + (now - s.LastRefill).TotalSeconds * s.LimitBytesPerSec);
            s.LastRefill = now;
            if (s.Tokens < cost) return false;
            s.Tokens -= cost;
            return true;
        }
    }

    private sealed class DeviceState
    {
        public readonly object TokenLock = new();
        public long TotalUp;
        public long TotalDown;
        public long LimitBytesPerSec;
        public bool Blocked;
        public double Tokens;
        public DateTime LastRefill = DateTime.UtcNow;
    }
}
