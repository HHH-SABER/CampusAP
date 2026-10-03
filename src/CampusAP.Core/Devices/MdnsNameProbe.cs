using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CampusAP.Core.Devices;

/// <summary>
/// mDNS 设备名主动探测：向设备 IP 的 5353 端口单播查询其广播的服务，
/// 从应答的 PTR 记录里取服务实例名（如 "Xiaomi 14"、"iPhone"）。
/// 现代手机普遍使用随机化 MAC（OUI 查不到厂商），mDNS 实例名是拿到设备友好名的最可靠来源：
/// Android 的投屏服务与 iOS 的 companion-link 都以设备名作为实例名。
/// 尽力而为：设备不响应或超时则返回 null，显示名回退主机名/IP。
/// </summary>
public static class MdnsNameProbe
{
    /// <summary>查询的服务类型与优先级（数值小者优先采用）</summary>
    private static readonly (string Service, int Priority)[] ServiceTypes =
    {
        ("_googlecast._tcp.local", 0),        // Android 投屏，实例名=设备名
        ("_companion-link._tcp.local", 0),    // iOS/macOS，实例名=设备名
        ("_airplay._tcp.local", 1),
        ("_androidtvremote2._tcp.local", 1),  // Android TV/盒子遥控
        ("_raop._tcp.local", 2),
        ("_ipp._tcp.local", 3),               // 打印/扫描
        ("_sftp-ssh._tcp.local", 4),
    };

    private const int MdnsPort = 5353;

    /// <summary>探测结果缓存（ip → 名称/null），会话内每设备只探一次</summary>
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>按 IP 尽力探测设备名；已探测过直接回缓存。失败返回 null。</summary>
    public static Task<string?> ProbeAsync(string ip, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(ip, out var cached)) return Task.FromResult(cached);
        return ProbeCoreAsync(ip, ct);
    }

    private static async Task<string?> ProbeCoreAsync(string ip, CancellationToken ct)
    {
        string? name = null;
        try
        {
            if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            {
                Cache[ip] = null;
                return null;
            }

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var endpoint = new IPEndPoint(addr, MdnsPort);
            ushort id = (ushort)Random.Shared.Next(1, short.MaxValue);
            var targets = new List<string> { "_services._dns-sd._udp.local" };
            targets.AddRange(ServiceTypes.Select(t => t.Service));
            foreach (var service in targets)
            {
                var packet = BuildQuery(service, id++);
                await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var found = new Dictionary<string, int>();
            while (DateTime.UtcNow < deadline)
            {
                var remain = deadline - DateTime.UtcNow;
                var receiveTask = udp.ReceiveAsync(ct).AsTask();
                var done = await Task.WhenAny(receiveTask, Task.Delay(remain, ct)).ConfigureAwait(false);
                if (done != receiveTask) break;
                var res = receiveTask.Result;
                if (!res.RemoteEndPoint.Address.Equals(addr)) continue; // 只收目标设备的应答
                ParseResponse(res.Buffer, found);
            }

            name = found.Count == 0
                ? null
                : found.OrderBy(kv => kv.Value).Select(kv => kv.Key).First();
        }
        catch (OperationCanceledException)
        {
            // 关闭中的探测：按失败处理
        }
        catch
        {
            // 探测属尽力而为，任何异常都静默
        }
        Cache[ip] = name;
        return name;
    }

    // ---- DNS 报文构造与解析（mDNS 与 DNS 同构，名字可压缩）----

    private static byte[] BuildQuery(string name, ushort id)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(ToNetwork_u16(id));
        w.Write(ToNetwork_u16(0));  // flags：标准查询
        w.Write(ToNetwork_u16(1));  // QDCOUNT
        w.Write(ToNetwork_u16(0));  // ANCOUNT
        w.Write(ToNetwork_u16(0));  // NSCOUNT
        w.Write(ToNetwork_u16(0));  // ARCOUNT
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }
        w.Write((byte)0);
        w.Write(ToNetwork_u16(12)); // QTYPE=PTR
        w.Write(ToNetwork_u16(1));  // QCLASS=IN
        w.Flush();
        return ms.ToArray();
    }

    private static void ParseResponse(byte[] data, Dictionary<string, int> found)
    {
        if (data.Length < 12) return;
        int qd = Read_u16(data, 4), an = Read_u16(data, 6), ns = Read_u16(data, 8), ar = Read_u16(data, 10);
        var pos = 12;
        for (var i = 0; i < qd && pos < data.Length; i++)
        {
            pos = SkipName(data, pos);
            pos += 4;
        }
        for (var i = 0; i < an + ns + ar && pos + 10 <= data.Length; i++)
        {
            pos = SkipName(data, pos); // 所有者名
            var type = Read_u16(data, pos);
            pos += 8; // TYPE(2) + CLASS(2) + TTL(4)
            var rdlen = Read_u16(data, pos);
            pos += 2;
            if (pos + rdlen > data.Length) break;
            if (type == 12)
            {
                ReadName(data, pos, out var ptr);
                var instance = ptr.Split('.')[0];
                if (instance.Length > 0 && !instance.StartsWith('_'))
                {
                    var service = ptr[(instance.Length + 1)..];
                    var priority = ServiceTypes.FirstOrDefault(t => t.Service == service).Priority;
                    found.TryAdd(instance, priority);
                }
            }
            pos += rdlen;
        }
    }

    private static ushort Read_u16(byte[] d, int pos) => (ushort)((d[pos] << 8) | d[pos + 1]);
    private static byte[] ToNetwork_u16(ushort v) => [(byte)(v >> 8), (byte)(v & 0xFF)];

    /// <summary>读一个（可能压缩的）域名，返回下一个读取位置</summary>
    private static int ReadName(byte[] d, int pos, out string name)
    {
        var sb = new StringBuilder();
        var p = pos;
        int? afterPointer = null;
        var jumps = 0;
        while (p < d.Length)
        {
            var len = d[p];
            if (len == 0)
            {
                afterPointer ??= p + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= d.Length) break;
                afterPointer ??= p + 2;
                if (++jumps > 32) break; // 压缩指针环路防御
                p = ((len & 0x3F) << 8) | d[p + 1];
                continue;
            }
            if (p + 1 + len > d.Length) break;
            sb.Append(Encoding.UTF8.GetString(d, p + 1, len)).Append('.');
            p += 1 + len;
        }
        name = sb.ToString().TrimEnd('.');
        return afterPointer ?? p;
    }

    private static int SkipName(byte[] d, int pos) => ReadName(d, pos, out _);
}
