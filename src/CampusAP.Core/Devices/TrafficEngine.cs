using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinDivertSharp;

namespace CampusAP.Core.Devices;

/// <summary>
/// 基于 WinDivert 的每设备流量引擎（v0.5.0 观察者/执行者架构，真机四轮取证定案）。
/// 需要管理员权限（加载 WinDivert 内核驱动）。
///
/// 背景：Win11 24H2 移动热点的 NAT 是 winnat 连接重定向型——客户端流量被重定向成本机流量，
/// 在 Network 层与宿主机自己的流量混合（无法按地址归属）；Forward 层能看到客户端原始地址的
/// "影子副本"，但丢弃影子不影响真实转发（真机实测：拉黑后 B站照刷）。
///
/// 双角色架构：
/// - **Forward 层 = 观察者**（Sniff 纯旁路）：从客户端原始五元组建立流映射表
///   (协议, 远端, NAT后本地端口) → 设备。TCP 靠 seq 关联同一包的 NAT 前后两遍副本；
///   UDP 靠流建立时刻的时间窗关联。
/// - **Network 层 = 执行者**（drop-and-divert）：宿主机全部流量在此层可见（含被重定向的客户端
///   流量），按流映射表查归属设备，做计数、限速（令牌桶丢包）、拉黑（整包丢弃）、TTL 伪装。
///   Network 层的丢包是真实生效的（标准 WinDivert 本机流量过滤语义）。
/// - 主机↔客户端的本地流量（137.x 互访、DHCP、mDNS）在 Network 层带原始设备地址，直接处理。
/// </summary>
public sealed class TrafficEngine : IDisposable
{
    /// <summary>Win10 移动热点默认私有网段（ICS 经典值 192.168.137.0/24；Win11 可能随机化，由 SetClients 动态补充）</summary>
    private const string DefaultSubnetBase = "192.168.137";

    /// <summary>TTL 伪装目标 = 宿主默认 TTL（Network 层出向包的 TTL 即线上 TTL，直接对齐宿主）。
    /// Win11 24H2 为 64（注册表动态读取），老系统 128。</summary>
    private readonly int _hostTtl;

    // ===== 设备表 =====
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();
    private readonly ConcurrentDictionary<uint, DeviceState> _devices4 = new();
    private readonly ConcurrentDictionary<V6Addr, DeviceState> _devices6 = new();

    // ===== 流映射表（Forward 观察者写，Network 执行者读）=====
    private readonly ConcurrentDictionary<PendingKey, PendingEntry> _pendingTcp = new();
    private readonly ConcurrentDictionary<PendingKey, PendingEntry> _pendingUdp = new();
    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _flows = new();

    // ===== 句柄与线程 =====
    private IntPtr _f4, _f6, _n4, _n6;
    private Thread? _f4Thread, _f6Thread, _n4Thread, _n6Thread;
    private volatile bool _running;

    private readonly object _lifecycleLock = new();

    // ---- 自诊断计数 ----
    private long _observePackets;
    private long _observeClient;
    private long _enforcePackets;
    private long _enforceDropped;
    private volatile bool _observerSeen;

    /// <summary>是否对热点上行包做 TTL 伪装（抹掉多设备指纹）</summary>
    public bool TtlSpoofEnabled { get; set; } = true;

    public bool IsRunning => _running;

    public TrafficEngine()
    {
        _hostTtl = ReadHostTtl();
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running) return;

            // 观察层必须全量抓取：既要 NAT 前副本（137.x，识别设备）也要 NAT 后副本（宿主地址，携带 NAT 端口）。
            // 若过滤器只放行 137 网段，NAT 后副本进不来，流映射表永远为空 → 执行层全部未映射（真机教训）。
            _f4 = WinDivert.WinDivertOpen("true", WinDivertLayer.Forward, 10, WinDivertOpenFlags.Sniff);
            _f6 = WinDivert.WinDivertOpen("ipv6", WinDivertLayer.Forward, 10, WinDivertOpenFlags.Sniff);
            _n4 = WinDivert.WinDivertOpen("!loopback", WinDivertLayer.Network, 0, WinDivertOpenFlags.None);
            _n6 = WinDivert.WinDivertOpen("ipv6 and !loopback", WinDivertLayer.Network, 0, WinDivertOpenFlags.None);

            if (_n4 == IntPtr.Zero && _n6 == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Logging.Log.Error($"引擎启动失败: 执行层句柄打不开 Win32={err}（观察层v4={_f4 != IntPtr.Zero} v6={_f6 != IntPtr.Zero}）");
                CloseAll();
                throw new InvalidOperationException(err switch
                {
                    5 => "流量管控需要管理员权限（WinDivert 驱动加载被拒绝）。",
                    2 => "未找到 WinDivert 驱动文件：WinDivert.dll 和 WinDivert64.sys 需与主程序同目录。",
                    577 or 1274 => "WinDivert 驱动加载被安全策略/杀毒软件阻止。",
                    _ => $"WinDivert 打开失败（Win32 错误码 {err}）。",
                });
            }

            _running = true;
            Logging.Log.Info($"引擎启动: 观察层v4={_f4 != IntPtr.Zero} v6={_f6 != IntPtr.Zero} 执行层v4={_n4 != IntPtr.Zero} v6={_n6 != IntPtr.Zero} 宿主TTL={_hostTtl} 设备数={_devices.Count}");
            Spawn(ref _f4Thread, ObserveLoop, _f4, true, "Observe-v4");
            Spawn(ref _f6Thread, ObserveLoop, _f6, false, "Observe-v6");
            Spawn(ref _n4Thread, EnforceLoop, _n4, true, "Enforce-v4");
            Spawn(ref _n6Thread, EnforceLoop, _n6, false, "Enforce-v6");
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _running = false;
            Logging.Log.Info($"引擎停止: 观察={Interlocked.Read(ref _observePackets)} 执行={Interlocked.Read(ref _enforcePackets)} 丢弃={Interlocked.Read(ref _enforceDropped)}");
            CloseAll();
            foreach (var t in new[] { _f4Thread, _f6Thread, _n4Thread, _n6Thread })
                try { t?.Join(1500); } catch { }
            _f4Thread = _f6Thread = _n4Thread = _n6Thread = null;
            _observerSeen = false;
            _pendingTcp.Clear();
            _pendingUdp.Clear();
            _flows.Clear();
        }
    }

    /// <summary>同步受管设备集合（由 UI 轮询线程调用，传入系统 tethering API 的客户端 IPv4+IPv6）</summary>
    public void SetClients(IEnumerable<string> ips)
    {
        var set = new HashSet<string>(ips);
        foreach (var ip in set) _devices.TryAdd(ip, new DeviceState());
        foreach (var kv in _devices)
            if (!set.Contains(kv.Key)) _devices.TryRemove(kv.Key, out _);

        _devices4.Clear();
        _devices6.Clear();
        foreach (var kv in _devices)
        {
            if (!IPAddress.TryParse(kv.Key, out var addr)) continue;
            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = addr.GetAddressBytes();
                _devices4.TryAdd((uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]), kv.Value);
            }
            else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                _devices6.TryAdd(new V6Addr(addr.GetAddressBytes()), kv.Value);
            }
        }

        Logging.Log.Info($"设备表同步: {set.Count} 台（{string.Join(", ", set)}）");
    }

    /// <summary>设置限速（字节/秒，0=不限）。即时生效。</summary>
    public void SetLimit(string ip, long bytesPerSec)
    {
        Logging.Log.Info($"{ip} 限速 → {(bytesPerSec == 0 ? "不限" : $"{bytesPerSec / 125000.0:N1} Mbps ({bytesPerSec} B/s)")}");
        if (_devices.TryGetValue(ip, out var s))
            lock (s.TokenLock)
            {
                s.LimitBytesPerSec = bytesPerSec;
                s.Tokens = Math.Min(s.Tokens, bytesPerSec);
            }
    }

    /// <summary>设置拉黑状态。即时生效（Network 层双向丢包）。</summary>
    public void SetBlocked(string ip, bool blocked)
    {
        Logging.Log.Info($"{ip} {(blocked ? "拉黑" : "解除拉黑")}");
        if (_devices.TryGetValue(ip, out var s)) s.Blocked = blocked;
    }

    /// <summary>读某设备累计上行/下行字节数（Network 层真实流量）</summary>
    public (long Up, long Down) GetTotals(string ip)
    {
        return _devices.TryGetValue(ip, out var s)
            ? (Interlocked.Read(ref s.TotalUp), Interlocked.Read(ref s.TotalDown))
            : (0, 0);
    }

    /// <summary>分层自诊断文本（UI 状态栏）</summary>
    public string GetCaptureDiagnostics()
    {
        var o = Interlocked.Read(ref _observePackets);
        var oc = Interlocked.Read(ref _observeClient);
        var n = Interlocked.Read(ref _enforcePackets);
        var d = Interlocked.Read(ref _enforceDropped);
        return _observerSeen
            ? $"映射 Forward({o:N0}包,设备{oc:N0}) · 管控 Network({n:N0}包,丢{d:N0}) · 流表{_flows.Count}"
            : $"Forward观察({o:N0}包)未见设备流量 · Network({n:N0}包)未映射";
    }

    /// <summary>两层累计收到的包数（供"抓包是否活着"的运行时判断）</summary>
    public long TotalPacketsSeen =>
        Interlocked.Read(ref _observePackets) + Interlocked.Read(ref _enforcePackets);

    public void Dispose() => Stop();

    // ---- 句柄与线程 ----

    private void Spawn(ref Thread? slot, Action<IntPtr, bool> loop, IntPtr handle, bool v4, string name)
    {
        slot = new Thread(() => loop(handle, v4)) { IsBackground = true, Name = name };
        slot.Start();
    }

    private void CloseAll()
    {
        foreach (var h in new[] { _f4, _f6, _n4, _n6 })
            if (h != IntPtr.Zero) WinDivert.WinDivertClose(h);
        _f4 = _f6 = _n4 = _n6 = IntPtr.Zero;
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

    // ---- 观察线程（Forward 层，Sniff 只读不拦截）----

    private void ObserveLoop(IntPtr handle, bool v4)
    {
        var buffer = new WinDivertBuffer();
        var addr = new WinDivertAddress();
        while (_running)
        {
            addr.Reset();
            uint len = 0;
            if (!WinDivert.WinDivertRecv(handle, buffer, ref addr, ref len))
            {
                if (!_running) break;
                Thread.Sleep(30);
                continue;
            }
            Interlocked.Increment(ref _observePackets);
            try { Observe(buffer, len, v4); }
            catch { /* 单包异常不影响观察循环 */ }
            // Sniff 模式：原始包自动继续，无需 Reinject
        }
    }

    /// <summary>从 Forward 层影子包提取设备流信息：第一遍（设备地址）记 pending，
    /// 第二遍（NAT 后地址）用 seq 关联出 NAT 端口，登记完整流映射。</summary>
    private void Observe(WinDivertBuffer buffer, uint len, bool v4)
    {
        if (!v4)
        {
            if (len < 60 || (buffer[0] >> 4) != 6) return;
            var src6 = ReadV6(buffer, 8);
            var dst6 = ReadV6(buffer, 24);
            var nextHeader = buffer[6];
            var srcDev = _devices6.GetValueOrDefault(src6);
            var dstDev = _devices6.GetValueOrDefault(dst6);

            if (srcDev is null && dstDev is null)
            {
                // NAT 后副本（若 v6 走 NAT66）：seq 关联；纯路由 v6 到不了这
                if (nextHeader == 6)
                    TryResolvePass2Tcp(6, src6, dst6, ReadU16(buffer, 40), ReadU16(buffer, 42), ReadU32(buffer, 44));
                return;
            }

            var remote = srcDev is not null ? dst6 : src6;
            var remotePort = srcDev is not null ? ReadU16(buffer, 42) : ReadU16(buffer, 40);
            var dev6 = srcDev ?? dstDev!;
            _observerSeen = true;
            Interlocked.Increment(ref _observeClient);
            if (nextHeader is 6 or 17)
            {
                var pending = new PendingKey(nextHeader, new RemoteKey(remote),
                    nextHeader == 6 ? ReadU32(buffer, 44) : 0);
                if (nextHeader == 6) _pendingTcp[pending] = new PendingEntry(dev6, DateTime.UtcNow.Ticks);
                else _pendingUdp[pending] = new PendingEntry(dev6, DateTime.UtcNow.Ticks);
            }
            return;
        }

        if (len < 20 || (buffer[0] >> 4) != 4) return;
        var src = ReadU32(buffer, 12);
        var dst = ReadU32(buffer, 16);
        var proto = buffer[9];
        var srcDev4 = _devices4.GetValueOrDefault(src);
        var dstDev4 = _devices4.GetValueOrDefault(dst);

        if (srcDev4 is null && dstDev4 is null)
        {
            // NAT 后副本：上行 remote=dst/local=src，下行 remote=src/local=dst，seq 关联
            if (proto == 6)
            {
                var ihl = (buffer[0] & 0x0F) * 4;
                if (len < ihl + 20) return;
                TryResolvePass2TcpV4(dst, ReadU16(buffer, ihl + 2), src, ReadU16(buffer, ihl), ReadU32(buffer, ihl + 4));
            }
            return;
        }

        if (proto is not (6 or 17)) return;
        var ihl2 = (buffer[0] & 0x0F) * 4;
        if (len < ihl2 + 4) return;
        var isUp = srcDev4 is not null;
        var rKey = new RemoteKey(isUp ? dst : src);
        var rPort = isUp ? ReadU16(buffer, ihl2 + 2) : ReadU16(buffer, ihl2);
        var dev = srcDev4 ?? dstDev4!;
        var seq = proto == 6 ? ReadU32(buffer, ihl2 + 4) : 0;

        _observerSeen = true;
        Interlocked.Increment(ref _observeClient);
        var pkey = new PendingKey(proto, rKey, seq);
        if (proto == 6) _pendingTcp[pkey] = new PendingEntry(dev, DateTime.UtcNow.Ticks);
        else _pendingUdp[pkey] = new PendingEntry(dev, DateTime.UtcNow.Ticks);
    }

    /// <summary>TCP：NAT 后副本用 seq 找回设备，登记 (远端, NAT端口) → 设备 的完整流映射。
    /// 上行副本 remote=dst/local=src；下行副本 remote=src/local=dst。</summary>
    private void TryResolvePass2TcpV4(uint dstIp, ushort dstPort, uint srcIp, ushort srcPort, uint seq)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_pendingTcp.TryRemove(new PendingKey(6, new RemoteKey(dstIp), seq), out var up))
            _flows[new FlowKey(6, new RemoteKey(dstIp), dstPort, srcPort)] = new FlowEntry(up!.Device, now);
        else if (_pendingTcp.TryRemove(new PendingKey(6, new RemoteKey(srcIp), seq), out var down))
            _flows[new FlowKey(6, new RemoteKey(srcIp), srcPort, dstPort)] = new FlowEntry(down!.Device, now);
    }

    private void TryResolvePass2Tcp(int proto, V6Addr src, V6Addr dst, ushort srcPort, ushort dstPort, uint seq)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_pendingTcp.TryRemove(new PendingKey(proto, new RemoteKey(dst), seq), out var up))
            _flows[new FlowKey(proto, new RemoteKey(dst), dstPort, srcPort)] = new FlowEntry(up!.Device, now);
        else if (_pendingTcp.TryRemove(new PendingKey(proto, new RemoteKey(src), seq), out var down))
            _flows[new FlowKey(proto, new RemoteKey(src), srcPort, dstPort)] = new FlowEntry(down!.Device, now);
    }

    // ---- 执行线程（Network 层，drop-and-divert：真正的丢包点）----

    private void EnforceLoop(IntPtr handle, bool v4)
    {
        var buffer = new WinDivertBuffer();
        var addr = new WinDivertAddress();
        var lastSweep = DateTime.UtcNow.Ticks;
        var lastErrLogged = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(60).Ticks;
        while (_running)
        {
            addr.Reset();
            uint len = 0;
            if (!WinDivert.WinDivertRecv(handle, buffer, ref addr, ref len))
            {
                if (!_running) break;
                // Recv 失败限频记录（首次+每 30 秒一条），便于诊断句柄被关/驱动异常
                if (DateTime.UtcNow.Ticks - lastErrLogged > TimeSpan.FromSeconds(30).Ticks)
                {
                    lastErrLogged = DateTime.UtcNow.Ticks;
                    Logging.Log.Warn($"Enforce{(v4 ? "-v4" : "-v6")} Recv 失败: Win32 {Marshal.GetLastWin32Error()}");
                }
                Thread.Sleep(30);
                continue;
            }
            Interlocked.Increment(ref _enforcePackets);

            var reinject = true;
            if (!addr.Loopback)
            {
                try { reinject = Enforce(buffer, len, v4, addr.Direction == WinDivertDirection.Outbound); }
                catch (Exception ex)
                {
                    reinject = true; // 异常时放行，宁漏勿断
                    if (DateTime.UtcNow.Ticks - lastErrLogged > TimeSpan.FromSeconds(30).Ticks)
                    {
                        lastErrLogged = DateTime.UtcNow.Ticks;
                        Logging.Log.Error($"Enforce{(v4 ? "-v4" : "-v6")} 异常: {ex.Message}");
                    }
                }
            }

            if (DateTime.UtcNow.Ticks - lastSweep > TimeSpan.FromSeconds(10).Ticks)
            {
                lastSweep = DateTime.UtcNow.Ticks;
                SweepStale();
            }

            if (reinject && len > 0)
                WinDivert.WinDivertSend(handle, buffer, len, ref addr);
        }
    }

    /// <summary>执行层：按流映射归属设备 → 计数/限速/拉黑/TTL 伪装。返回 false = 丢弃该包。</summary>
    private bool Enforce(WinDivertBuffer buffer, uint len, bool v4, bool outbound)
    {
        DeviceState? dev;
        int ipProto;
        RemoteKey remote;
        ushort remotePort, localPort;
        bool isLocalDeviceTraffic;

        if (v4)
        {
            if (len < 20 || (buffer[0] >> 4) != 4) return true;
            var src = ReadU32(buffer, 12);
            var dst = ReadU32(buffer, 16);
            ipProto = buffer[9];
            var srcDev = _devices4.GetValueOrDefault(src);
            var dstDev = _devices4.GetValueOrDefault(dst);
            isLocalDeviceTraffic = srcDev is not null || dstDev is not null;

            if (isLocalDeviceTraffic)
            {
                dev = srcDev ?? dstDev!;
                remote = new RemoteKey(srcDev is not null ? dst : src);
                remotePort = ReadU16(buffer, srcDev is not null ? 18 : 12);
                localPort = ReadU16(buffer, srcDev is not null ? 16 : 14);
            }
            else
            {
                if (ipProto is not (6 or 17)) return true; // ICMP 等不归设备管
                var ihl = (buffer[0] & 0x0F) * 4;
                if (len < ihl + 4) return true;
                var srcPort = ReadU16(buffer, ihl);
                var dstPort = ReadU16(buffer, ihl + 2);
                // 客户端重定向流量：按流映射表双向各试一次
                if (!_flows.TryGetValue(new FlowKey(ipProto, new RemoteKey(dst), dstPort, srcPort), out var fe4))
                    _flows.TryGetValue(new FlowKey(ipProto, new RemoteKey(src), srcPort, dstPort), out fe4);
                dev = fe4?.Device;
                if (dev is null) return true; // 未映射（宿主自身流量等）：原样放行
                fe4!.LastSeen = DateTime.UtcNow.Ticks;
                remote = new RemoteKey(dst);
                remotePort = dstPort; localPort = srcPort;
                if (!outbound) { remote = new RemoteKey(src); remotePort = srcPort; localPort = dstPort; }
            }
        }
        else
        {
            if (len < 60 || (buffer[0] >> 4) != 6) return true;
            var src6 = ReadV6(buffer, 8);
            var dst6 = ReadV6(buffer, 24);
            ipProto = buffer[6];
            var srcDev = _devices6.GetValueOrDefault(src6);
            var dstDev = _devices6.GetValueOrDefault(dst6);
            isLocalDeviceTraffic = srcDev is not null || dstDev is not null;

            if (isLocalDeviceTraffic)
            {
                dev = srcDev ?? dstDev!;
                remote = new RemoteKey(srcDev is not null ? dst6 : src6);
                remotePort = ReadU16(buffer, srcDev is not null ? 42 : 40);
                localPort = ReadU16(buffer, srcDev is not null ? 40 : 42);
            }
            else
            {
                if (ipProto is not (6 or 17)) return true;
                var srcPort = ReadU16(buffer, 40);
                var dstPort = ReadU16(buffer, 42);
                if (!_flows.TryGetValue(new FlowKey(ipProto, new RemoteKey(dst6), dstPort, srcPort), out var fe6))
                    _flows.TryGetValue(new FlowKey(ipProto, new RemoteKey(src6), srcPort, dstPort), out fe6);
                dev = fe6?.Device;
                if (dev is null) return true;
                fe6!.LastSeen = DateTime.UtcNow.Ticks;
                remote = new RemoteKey(dst6);
                remotePort = dstPort; localPort = srcPort;
                if (!outbound) { remote = new RemoteKey(src6); remotePort = srcPort; localPort = dstPort; }
            }
        }

        // UDP 新流：观察层时间窗关联（TCP 已由 seq 关联）
        if (ipProto == 17 && !isLocalDeviceTraffic && !_flows.ContainsKey(new FlowKey(ipProto, remote, remotePort, localPort)))
        {
            var key = new PendingKey(ipProto, remote, 0);
            if (_pendingUdp.TryGetValue(key, out var p) && DateTime.UtcNow.Ticks - p.Tick < TimeSpan.FromSeconds(2).Ticks)
                _flows[new FlowKey(ipProto, remote, remotePort, localPort)] = new FlowEntry(p.Device, DateTime.UtcNow.Ticks);
        }

        // 计数（outbound=设备上行，inbound=设备下行）
        if (outbound) Interlocked.Add(ref dev.TotalUp, len);
        else Interlocked.Add(ref dev.TotalDown, len);

        // 管控：拉黑 / 限速（真实丢包点——不 Send 包就没了）
        if (dev.Blocked) { Interlocked.Increment(ref _enforceDropped); return false; }
        if (dev.LimitBytesPerSec > 0 && !TryConsumeToken(dev, len))
        {
            Interlocked.Increment(ref _enforceDropped);
            return false;
        }

        // TTL 伪装：设备上行出向的互联网流量，TTL 对齐宿主（Network 层的 TTL 就是线上 TTL）
        if (v4 && TtlSpoofEnabled && outbound && ipProto == 6 && !isLocalDeviceTraffic)
        {
            var ihl = (buffer[0] & 0x0F) * 4;
            if (len >= ihl && buffer[8] != _hostTtl)
            {
                buffer[8] = (byte)_hostTtl;
                FixIpChecksum(buffer, len);
            }
        }

        return true;
    }

    private void SweepStale()
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var kv in _flows)
            if (now - kv.Value.LastSeen > TimeSpan.FromMinutes(10).Ticks)
                _flows.TryRemove(kv.Key, out _);
        foreach (var kv in _pendingTcp)
            if (now - kv.Value.Tick > TimeSpan.FromSeconds(30).Ticks)
                _pendingTcp.TryRemove(kv.Key, out _);
        foreach (var kv in _pendingUdp)
            if (now - kv.Value.Tick > TimeSpan.FromSeconds(5).Ticks)
                _pendingUdp.TryRemove(kv.Key, out _);

        // 统计心跳（30 秒一条）：观察/执行/丢弃/流表/映射覆盖
        if (now - _lastHeartbeat > TimeSpan.FromSeconds(30).Ticks)
        {
            _lastHeartbeat = now;
            var mapped = _flows.Count;
            var top = string.Join(", ", _devices.Values
                .Where(d => Interlocked.Read(ref d.TotalDown) > 0 || Interlocked.Read(ref d.TotalUp) > 0)
                .Take(4));
            Logging.Log.Info($"心跳 观察={Interlocked.Read(ref _observePackets):N0}(设备{Interlocked.Read(ref _observeClient):N0}) 执行={Interlocked.Read(ref _enforcePackets):N0} 丢弃={Interlocked.Read(ref _enforceDropped):N0} 流表={mapped} [{top}]");
        }
    }

    private long _lastHeartbeat;

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

    /// <summary>重算 IPv4 头校验和（改了 TTL 后必须调用）。校验和字段 offset 10-11；12-15 是源地址。</summary>
    private static void FixIpChecksum(WinDivertBuffer buffer, uint len)
    {
        int ihl = (buffer[0] & 0x0F) * 4;
        if (len < ihl || ihl < 20) return;
        buffer[10] = 0;
        buffer[11] = 0;
        uint sum = 0;
        for (int i = 0; i < ihl; i += 2)
            sum += (ushort)((buffer[i] << 8) | buffer[i + 1]);
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        ushort checksum = (ushort)(~sum);
        buffer[10] = (byte)(checksum >> 8);
        buffer[11] = (byte)(checksum & 0xFF);
    }

    private static uint ReadU32(WinDivertBuffer b, int off)
        => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    private static ushort ReadU16(WinDivertBuffer b, int off)
        => (ushort)((b[off] << 8) | b[off + 1]);

    private static V6Addr ReadV6(WinDivertBuffer b, int off)
    {
        var tmp = new byte[16];
        for (var i = 0; i < 16; i++) tmp[i] = b[off + i];
        return new V6Addr(tmp);
    }

    // ---- 键与条目类型 ----

    /// <summary>IPv6 地址键（128 位折两个 ulong）</summary>
    private readonly record struct V6Addr(ulong Hi, ulong Lo)
    {
        public V6Addr(byte[] b) : this(
            ((ulong)b[0] << 56) | ((ulong)b[1] << 48) | ((ulong)b[2] << 40) | ((ulong)b[3] << 32) |
            ((ulong)b[4] << 24) | ((ulong)b[5] << 16) | ((ulong)b[6] << 8) | b[7],
            ((ulong)b[8] << 56) | ((ulong)b[9] << 48) | ((ulong)b[10] << 40) | ((ulong)b[11] << 32) |
            ((ulong)b[12] << 24) | ((ulong)b[13] << 16) | ((ulong)b[14] << 8) | b[15]) { }
    }

    /// <summary>远端键：v4 折进 Hi（主机序 uint），v6 占满 128 位</summary>
    private readonly record struct RemoteKey(ulong Hi, ulong Lo)
    {
        public RemoteKey(uint v4) : this(v4, 0) { }
        public RemoteKey(V6Addr v6) : this(v6.Hi, v6.Lo) { }
    }

    /// <summary>TCP pending 键：协议 + 远端 + seq（同一包 NAT 前后 seq 不变）</summary>
    private readonly record struct PendingKey(int Proto, RemoteKey Remote, uint Seq);

    private sealed record PendingEntry(DeviceState Device, long Tick);

    /// <summary>流键：协议 + 远端 + NAT 后本地端口</summary>
    private readonly record struct FlowKey(int Proto, RemoteKey Remote, ushort RemotePort, ushort LocalPort);

    private sealed class FlowEntry
    {
        public DeviceState Device;
        public long LastSeen;
        public FlowEntry(DeviceState dev, long tick) { Device = dev; LastSeen = tick; }
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
