using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinDivertSharp;

namespace CampusAP.Core.Devices;

/// <summary>
/// 基于 WinDivert（用户态包过滤）的每设备流量引擎：
/// 按设备计数上下行、令牌桶限速（超额丢包，由 TCP 拥塞控制自然回压）、整包丢弃实现拉黑。
/// 需要管理员权限（加载 WinDivert 内核驱动）。
///
/// 抓包架构（v0.4.0 定案，探针三轮真机取证）：
/// - **Forward 层（主）**：WFP IPFORWARD 层。Win11 24H2 的移动热点 NAT（winnat 连接重定向型）下，
///   客户端转发的每个包在 Forward 层出现两遍：第一遍 NAT 前（src/dst=192.168.137.x 原始地址），
///   第二遍 NAT 后（src/dst=宿主出口 IP）。第一遍用于设备归属、计数与管控；TCP 按"五元组+seq"
///   去重防止同一包被计两次。
/// - **Network 层（兜底）**：主机↔客户端本地流量（如 DHCP ACK）只在此层出现；Forward 层未见过
///   任何客户端包时（Forward 不可用的环境）由它承担计数与管控（旧行为），避免重复计数。
/// - **过滤器纪律**：只允许 IPv4 比较子句。把 `ipv6.SrcAddr >= fe80:: …` 等 IPv6 比较子句与 IPv4
///   子句并列在同一过滤器里，会使整个过滤器静默失配（探针对照实验实证：含 v6 子句 0 包，
///   纯 v4 同点位 31.9 万包）。IPv6 管控是已知缺口：v6 包不进本引擎，正常流通。
/// </summary>
public sealed class TrafficEngine : IDisposable
{
    /// <summary>Win10 移动热点默认私有网段（ICS 经典值 192.168.137.0/24；Win11 可能随机化，由 SetClients 动态补充）</summary>
    private const string DefaultSubnetBase = "192.168.137";

    /// <summary>TTL 伪装目标：Forward 第一遍捕获点在"入向转发减 1 之后、出向转发减 1 之前"，
    /// 设为 宿主TTL+1 则出口线上正是宿主 TTL（Win11 24H2 默认 TTL=64，老系统 128，注册表动态读取）</summary>
    private readonly int _spoofTtl;

    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();

    /// <summary>过滤器已覆盖的 IPv4 /24 网段基（前三个八位组）。默认恒含 ICS 经典网段。</summary>
    private readonly HashSet<string> _subnetBases = new() { DefaultSubnetBase };

    private readonly object _lifecycleLock = new();

    /// <summary>TCP 转发包去重：同一包在 Forward 层出现两遍，按五元组+seq 只计/管控一遍</summary>
    private readonly HashSet<(uint SrcIp, uint DstIp, ushort SrcPort, ushort DstPort, uint Seq)> _tcpSeen = new();
    private readonly object _tcpSeenLock = new();
    private long _tcpSeenLastClear = DateTime.UtcNow.Ticks;

    private IntPtr _forwardHandle;
    private IntPtr _networkHandle;
    private Thread? _forwardThread;
    private Thread? _networkThread;
    private volatile bool _running;
    private bool _forwardAvailable;

    // ---- 分层自诊断计数 ----
    private long _forwardPackets;       // Forward 层见过的包（含非客户端与第二遍）
    private long _forwardClientPackets; // Forward 层匹配到设备的"第一遍"包
    private long _networkPackets;       // Network 层见过的包
    private volatile bool _forwardSeen; // Forward 层见过客户端包 → Network 层退出计数兜底

    /// <summary>是否对热点上行包做 TTL 伪装（抹掉多设备指纹）</summary>
    public bool TtlSpoofEnabled { get; set; } = true;

    public bool IsRunning => _running;

    /// <summary>两层累计收到的包数（含未匹配设备的），供"抓包是否活着"的运行时判断</summary>
    public long TotalPacketsSeen =>
        Interlocked.Read(ref _forwardPackets) + Interlocked.Read(ref _networkPackets);

    public TrafficEngine()
    {
        _spoofTtl = ReadHostTtl() + 1;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running) return;
            var filter = BuildFilter(_subnetBases);
            _forwardHandle = OpenHandle(filter, WinDivertLayer.Forward, out var forwardErr);
            _networkHandle = OpenHandle(filter, WinDivertLayer.Network, out var networkErr);

            if (_forwardHandle == IntPtr.Zero && _networkHandle == IntPtr.Zero)
                throw new InvalidOperationException(DescribeOpenError(forwardErr != 0 ? forwardErr : networkErr));

            _forwardAvailable = _forwardHandle != IntPtr.Zero;
            _running = true;
            _forwardThread = new Thread(() => PacketLoop(WinDivertLayer.Forward))
            {
                IsBackground = true,
                Name = "TrafficEngine-Forward",
            };
            _forwardThread.Start();
            _networkThread = new Thread(() => PacketLoop(WinDivertLayer.Network))
            {
                IsBackground = true,
                Name = "TrafficEngine-Network",
            };
            _networkThread.Start();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _running = false;
            CloseHandles();
            try { _forwardThread?.Join(1500); } catch { }
            try { _networkThread?.Join(1500); } catch { }
            _forwardThread = null;
            _networkThread = null;
            _forwardSeen = false;
            _forwardAvailable = false;
            lock (_tcpSeenLock) _tcpSeen.Clear();
        }
    }

    /// <summary>同步受管设备集合（由 UI 轮询线程调用，传入系统 tethering API 的客户端 IP）。
    /// 若出现过滤器未覆盖的 IPv4 网段（Win11 随机网段），重建过滤器并重开句柄。</summary>
    public void SetClients(IEnumerable<string> ips)
    {
        var set = new HashSet<string>(ips);
        foreach (var ip in set) _devices.TryAdd(ip, new DeviceState());
        foreach (var kv in _devices)
            if (!set.Contains(kv.Key)) _devices.TryRemove(kv.Key, out _);

        var bases = new HashSet<string>();
        foreach (var ip in set)
            if (TryGetSubnetBase(ip, out var baseText))
                bases.Add(baseText);
        bases.Add(DefaultSubnetBase);
        if (bases.Count > 8) // 网段数异常（不该发生）：只留默认网段防过滤器膨胀
            bases = new HashSet<string> { DefaultSubnetBase };

        lock (_lifecycleLock)
        {
            if (_subnetBases.IsSubsetOf(bases) && bases.IsSubsetOf(_subnetBases)) return;
            _subnetBases.Clear();
            foreach (var b in bases) _subnetBases.Add(b);
            if (!_running) return;
            ReopenHandlesLocked();
        }
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

    /// <summary>分层抓包自诊断文本（UI 状态栏展示，真机验证时一眼判断哪层在工作）</summary>
    public string GetCaptureDiagnostics()
    {
        var f = Interlocked.Read(ref _forwardPackets);
        var fc = Interlocked.Read(ref _forwardClientPackets);
        var n = Interlocked.Read(ref _networkPackets);
        return _forwardSeen
            ? $"抓包层 Forward({f:N0}包,设备{fc:N0}) + Network兜底({n:N0})"
            : _forwardAvailable
                ? $"Forward({f:N0}包)未见设备流量，Network兜底({n:N0}包)"
                : $"Forward不可用，Network({n:N0}包)";
    }

    public void Dispose() => Stop();

    // ---- 句柄生命周期 ----

    private static IntPtr OpenHandle(string filter, WinDivertLayer layer, out int error)
    {
        var handle = WinDivert.WinDivertOpen(filter, layer, 0, WinDivertOpenFlags.None);
        error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    private static string DescribeOpenError(int err) => err switch
    {
        5 => "流量管控需要管理员权限（WinDivert 驱动加载被拒绝）。",
        2 => "未找到 WinDivert 驱动文件：WinDivert.dll 和 WinDivert64.sys 需与主程序同目录。",
        577 or 1274 => "WinDivert 驱动加载被安全策略/杀毒软件阻止。",
        _ => $"WinDivert 打开失败（Win32 错误码 {err}）。",
    };

    private void CloseHandles()
    {
        if (_forwardHandle != IntPtr.Zero)
        {
            WinDivert.WinDivertClose(_forwardHandle);
            _forwardHandle = IntPtr.Zero;
        }
        if (_networkHandle != IntPtr.Zero)
        {
            WinDivert.WinDivertClose(_networkHandle);
            _networkHandle = IntPtr.Zero;
        }
    }

    /// <summary>用当前网段重建过滤器并重开两个句柄（须持 _lifecycleLock 且 _running）。
    /// 阻塞在旧句柄 Recv 上的线程会随 Close 返回失败，随后读到新句柄继续。</summary>
    private void ReopenHandlesLocked()
    {
        CloseHandles();
        var filter = BuildFilter(_subnetBases);
        _forwardHandle = OpenHandle(filter, WinDivertLayer.Forward, out var forwardErr);
        _networkHandle = OpenHandle(filter, WinDivertLayer.Network, out _);
        _forwardAvailable = _forwardHandle != IntPtr.Zero;
        if (_forwardHandle == IntPtr.Zero && _networkHandle == IntPtr.Zero)
            throw new InvalidOperationException(DescribeOpenError(forwardErr));
    }

    /// <summary>构造过滤器。纪律：只允许 IPv4 比较子句——IPv6 比较子句混入会使整个过滤器
    /// 静默失配（探针对照实验实证，见类注释）。</summary>
    private static string BuildFilter(IEnumerable<string> bases)
    {
        var v4 = string.Join(" or ", bases.Select(b =>
            $"(ip.SrcAddr >= {b}.0 and ip.SrcAddr <= {b}.255) or " +
            $"(ip.DstAddr >= {b}.0 and ip.DstAddr <= {b}.255)"));
        return $"({v4})";
    }

    private static bool TryGetSubnetBase(string ip, out string baseText)
    {
        baseText = "";
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var octets = addr.ToString().Split('.');
        if (octets.Length != 4) return false;
        baseText = $"{octets[0]}.{octets[1]}.{octets[2]}";
        return true;
    }

    /// <summary>读宿主默认 TTL（注册表 Tcpip\Parameters\DefaultTtl；Win11 24H2 为 64，老系统 128）</summary>
    private static int ReadHostTtl()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            if (key?.GetValue("DefaultTtl") is int ttl && ttl is > 0 and <= 255) return ttl;
        }
        catch
        {
            // 读不到就用 Win11 24H2 实测默认值
        }
        return 64;
    }

    // ---- 抓包线程 ----

    private void PacketLoop(WinDivertLayer layer)
    {
        var buffer = new WinDivertBuffer();
        var addr = new WinDivertAddress();
        while (_running)
        {
            var handle = layer == WinDivertLayer.Forward ? _forwardHandle : _networkHandle;
            if (handle == IntPtr.Zero)
            {
                Thread.Sleep(50); // 句柄重开间隙
                continue;
            }
            addr.Reset();
            uint len = 0;
            if (!WinDivert.WinDivertRecv(handle, buffer, ref addr, ref len))
            {
                if (!_running) break;
                Thread.Sleep(50); // 句柄已关闭/错误：让出 CPU 等重开
                continue;
            }

            if (layer == WinDivertLayer.Forward)
            {
                Interlocked.Increment(ref _forwardPackets);
                ProcessForwardPacket(buffer, len, ref addr);
            }
            else
            {
                Interlocked.Increment(ref _networkPackets);
                ProcessNetworkPacket(buffer, len, ref addr);
            }
        }
    }

    /// <summary>Forward 层（转发路径）：设备流量计数 + TTL 伪装 + 限速/拉黑。
    /// 每个转发包经过本层两遍（NAT 前后），TCP 按"五元组+seq"去重只处理第一遍。</summary>
    private void ProcessForwardPacket(WinDivertBuffer buffer, uint len, ref WinDivertAddress addr)
    {
        if (len < 20 || (buffer[0] >> 4) != 4)
        {
            Reinject(_forwardHandle, buffer, len, ref addr); // IPv6 等：直接放行（v6 管控为已知缺口）
            return;
        }

        var src = IpToString(buffer, 12);
        var dst = IpToString(buffer, 16);
        var srcIsDevice = _devices.TryGetValue(src, out var upState);
        var dstIsDevice = _devices.TryGetValue(dst, out var downState);
        if (!srcIsDevice && !dstIsDevice)
        {
            Reinject(_forwardHandle, buffer, len, ref addr);
            return;
        }

        // 第二遍去重：TCP 同包 NAT 前后两遍五元组+seq 相同；UDP 靠 IP ID 可能去不净，允许少量重复计数
        if (IsDuplicateSecondPass(buffer, len))
        {
            Reinject(_forwardHandle, buffer, len, ref addr);
            return;
        }

        _forwardSeen = true;
        Interlocked.Increment(ref _forwardClientPackets);
        if (srcIsDevice) Interlocked.Add(ref upState!.TotalUp, len);
        if (dstIsDevice) Interlocked.Add(ref downState!.TotalDown, len);

        var owner = srcIsDevice ? upState : downState; // 上行按源设备、下行按目的设备管控
        if (owner!.Blocked) return;                    // 拉黑：双向丢弃（第一遍丢弃即死，第二遍不存在）
        if (owner.LimitBytesPerSec > 0 && !TryConsumeToken(owner, len))
            return;                                    // 限速：超额丢弃

        // TTL 伪装：仅客户端→外网的上行包（client↔client 互访不动）。
        // 捕获点在"入向转发减 1 后、出向转发减 1 前"，故设 宿主TTL+1，出口线上即宿主 TTL。
        if (TtlSpoofEnabled && srcIsDevice && !dstIsDevice && buffer[8] != _spoofTtl)
        {
            buffer[8] = (byte)_spoofTtl;
            FixIpChecksum(buffer, len);
        }

        Reinject(_forwardHandle, buffer, len, ref addr);
    }

    /// <summary>识别 Forward 层的第二遍（同一包 NAT 后的副本）。TCP 依据五元组+seq；
    /// 每 5 秒整体清空一次旧键防集合膨胀（重传窗口内的旧包可能被重计，量级可忽略）。</summary>
    private bool IsDuplicateSecondPass(WinDivertBuffer buffer, uint len)
    {
        if (buffer[9] != 6) return false; // 只对 TCP 去重
        var ihl = (buffer[0] & 0x0F) * 4;
        if (len < ihl + 20) return false;

        uint srcIp = (uint)((buffer[12] << 24) | (buffer[13] << 16) | (buffer[14] << 8) | buffer[15]);
        uint dstIp = (uint)((buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19]);
        ushort srcPort = (ushort)((buffer[ihl] << 8) | buffer[ihl + 1]);
        ushort dstPort = (ushort)((buffer[ihl + 2] << 8) | buffer[ihl + 3]);
        uint seq = (uint)((buffer[ihl + 4] << 24) | (buffer[ihl + 5] << 16) | (buffer[ihl + 6] << 8) | buffer[ihl + 7]);
        var key = (SrcIp: srcIp, DstIp: dstIp, SrcPort: srcPort, DstPort: dstPort, Seq: seq);

        var now = DateTime.UtcNow.Ticks;
        lock (_tcpSeenLock)
        {
            if (now - _tcpSeenLastClear > TimeSpan.FromSeconds(5).Ticks)
            {
                _tcpSeen.Clear();
                _tcpSeenLastClear = now;
            }
            return !_tcpSeen.Add(key);
        }
    }

    /// <summary>Network 层（主机本地路径）兜底：主机↔客户端本地流量始终计数；
    /// Forward 层不可用/未见流量时按旧行为承担计数与管控。</summary>
    private void ProcessNetworkPacket(WinDivertBuffer buffer, uint len, ref WinDivertAddress addr)
    {
        if (len < 20 || (buffer[0] >> 4) != 4)
        {
            Reinject(_networkHandle, buffer, len, ref addr);
            return;
        }

        var src = IpToString(buffer, 12);
        var dst = IpToString(buffer, 16);
        var srcIsDevice = _devices.TryGetValue(src, out var upState);
        var dstIsDevice = _devices.TryGetValue(dst, out var downState);
        if (!srcIsDevice && !dstIsDevice)
        {
            Reinject(_networkHandle, buffer, len, ref addr);
            return;
        }

        var degraded = !_forwardSeen; // Forward 层已接管设备流量后，此层只补主机本地包
        if (srcIsDevice) Interlocked.Add(ref upState!.TotalUp, len);
        if (dstIsDevice) Interlocked.Add(ref downState!.TotalDown, len);
        if (!degraded) { Reinject(_networkHandle, buffer, len, ref addr); return; }

        var state = srcIsDevice ? upState : downState;
        if (state!.Blocked) return;
        if (state.LimitBytesPerSec > 0 && !TryConsumeToken(state, len))
            return;

        if (TtlSpoofEnabled && srcIsDevice && !dstIsDevice && buffer[8] != _spoofTtl)
        {
            buffer[8] = (byte)_spoofTtl;
            FixIpChecksum(buffer, len);
        }

        Reinject(_networkHandle, buffer, len, ref addr);
    }

    private static void Reinject(IntPtr handle, WinDivertBuffer buffer, uint len, ref WinDivertAddress addr)
        => WinDivert.WinDivertSend(handle, buffer, len, ref addr);

    private static string IpToString(WinDivertBuffer buffer, int offset)
        => $"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}";

    /// <summary>重算 IPv4 头校验和（改了 TTL 后必须调用，否则包被丢弃）。
    /// 校验和字段在 offset 10-11；12-15 是源地址，绝不能写。</summary>
    private static void FixIpChecksum(WinDivertBuffer buffer, uint len)
    {
        int ihl = (buffer[0] & 0x0F) * 4;   // IP 头长度
        if (len < ihl || ihl < 20) return;

        buffer[10] = 0;
        buffer[11] = 0;

        uint sum = 0;
        for (int i = 0; i < ihl; i += 2)
        {
            ushort word = (ushort)((buffer[i] << 8) | buffer[i + 1]);
            sum += word;
        }
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        ushort checksum = (ushort)(~sum);
        buffer[10] = (byte)(checksum >> 8);
        buffer[11] = (byte)(checksum & 0xFF);
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
