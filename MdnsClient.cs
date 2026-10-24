using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PengchuanTray;

/// <summary>解析出来的一条 DNS 资源记录（只保留 mDNS 发现需要的字段）</summary>
public sealed class MdnsRecord
{
    public string Name { get; init; } = string.Empty;
    public ushort Type { get; init; }
    public string? Target { get; init; }
    public ushort Port { get; init; }
    public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();
    public string? Address { get; init; }
}

/// <summary>
/// 极简 mDNS 客户端：自己建 UDP socket（必须开 SO_REUSEADDR，Windows 的 DNS 客户端服务已占用 5353）、
/// 加入 224.0.0.251 组播组、发 PTR/SRV/TXT/A 查询并解析应答。
///
/// 为什么不用 Makaretu.Dns.Multicast：实测在 .NET 10 / Windows 11 上它的 MulticastService
/// 启动后收不到任何应答（同一时刻 Node 的 multicast-dns 能正常收到手机的 PTR），
/// 而它没有暴露 SO_REUSEADDR 等必要选项。为保证「装上就能发现」才自建这 200 行。
/// </summary>
public sealed class MdnsClient : IDisposable
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;
    public const ushort TypeSrv = 33;

    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");

    private readonly object _gate = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;

    /// <summary>收到一批资源记录（应答报文解析结果）</summary>
    public event Action<IReadOnlyList<MdnsRecord>>? RecordsReceived;

    /// <summary>诊断信息（--probe 输出，便于排查收不到应答的问题）</summary>
    public List<string> Diagnostics { get; } = new();

    public void Start()
    {
        if (_socket is not null)
        {
            return;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows 上 5353 已被系统 mDNS 服务占用，必须允许复用端口
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress));

        // 多网卡时逐个加入组播组（Wi-Fi 与有线同时存在）
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                var ipv4 = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is null)
                {
                    continue;
                }
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(MulticastAddress, ipv4.Address));
                    lock (_gate)
                    {
                        Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 加入组播：{nic.Name} {ipv4.Address}");
                    }
                }
                catch
                {
                    // 单网卡加入失败忽略
                }
            }
        }
        catch
        {
            // 网卡枚举失败不影响默认组播组
        }

        _socket = socket;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => QueryLoopAsync(_cts.Token));
        lock (_gate)
        {
            Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} mDNS socket 已就绪（5353 复用）");
        }
    }

    /// <summary>发送一个查询（PTR 服务发现 / SRV+TXT 实例解析 / A 地址解析）</summary>
    public void Query(string name, ushort type)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }
        try
        {
            var packet = BuildQuery(name, type);
            socket.SendTo(packet, new IPEndPoint(MulticastAddress, 5353));
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 发送查询失败：{ex.Message}");
            }
        }
    }

    private async Task QueryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket!;
        var buffer = new byte[9000];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, remote, ct);
                var records = DnsWire.Parse(buffer, result.ReceivedBytes);
                if (records.Count > 0)
                {
                    RecordsReceived?.Invoke(records);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (Diagnostics.Count < 200)
                    {
                        Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 接收异常：{ex.Message}");
                    }
                }
            }
        }
    }

    // ---------------- 查询报文构造 ----------------

    private static byte[] BuildQuery(string name, ushort type)
    {
        using var stream = new MemoryStream();
        // 事务 ID 0（mDNS 惯例）、flags 0、QDCOUNT=1，其余 0
        stream.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        WriteName(stream, name);
        stream.WriteByte((byte)(type >> 8));
        stream.WriteByte((byte)(type & 0xFF));
        stream.WriteByte(0); // CLASS IN + 无 cache-flush 位
        stream.WriteByte(1);
        return stream.ToArray();
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _socket?.Close();
        }
        catch
        {
            // 忽略
        }
        _socket = null;
    }
}

/// <summary>DNS 报文解析（含名字压缩指针），只支持 mDNS 发现用到的记录类型</summary>
internal static class DnsWire
{
    public static List<MdnsRecord> Parse(byte[] buffer, int length)
    {
        var records = new List<MdnsRecord>();
        if (length < 12)
        {
            return records;
        }
        var questions = ReadUInt16(buffer, 4);
        var answers = ReadUInt16(buffer, 6);
        var authorities = ReadUInt16(buffer, 8);
        var additionals = ReadUInt16(buffer, 10);
        var offset = 12;

        for (var i = 0; i < questions; i++)
        {
            offset = ReadName(buffer, offset, out _);
            offset += 4; // QTYPE + QCLASS
            if (offset > length)
            {
                return records;
            }
        }

        var total = answers + authorities + additionals;
        for (var i = 0; i < total; i++)
        {
            if (offset >= length)
            {
                break;
            }
            offset = ReadName(buffer, offset, out var name);
            if (offset + 10 > length)
            {
                break;
            }
            var type = ReadUInt16(buffer, offset);
            var dataLength = ReadUInt16(buffer, offset + 8);
            var dataOffset = offset + 10;
            if (dataOffset + dataLength > length)
            {
                break;
            }

            switch (type)
            {
                case MdnsClient.TypePtr:
                {
                    ReadName(buffer, dataOffset, out var target);
                    records.Add(new MdnsRecord { Name = name, Type = type, Target = target });
                    break;
                }
                case MdnsClient.TypeSrv:
                {
                    var port = ReadUInt16(buffer, dataOffset + 4);
                    ReadName(buffer, dataOffset + 6, out var target);
                    records.Add(new MdnsRecord { Name = name, Type = type, Port = port, Target = target });
                    break;
                }
                case MdnsClient.TypeTxt:
                {
                    var texts = new List<string>();
                    var cursor = dataOffset;
                    while (cursor < dataOffset + dataLength)
                    {
                        var len = buffer[cursor];
                        cursor++;
                        if (cursor + len > dataOffset + dataLength)
                        {
                            break;
                        }
                        texts.Add(Encoding.UTF8.GetString(buffer, cursor, len));
                        cursor += len;
                    }
                    records.Add(new MdnsRecord { Name = name, Type = type, Texts = texts });
                    break;
                }
                case MdnsClient.TypeA:
                {
                    if (dataLength == 4)
                    {
                        var ip = new IPAddress(new[] { buffer[dataOffset], buffer[dataOffset + 1], buffer[dataOffset + 2], buffer[dataOffset + 3] });
                        records.Add(new MdnsRecord { Name = name, Type = type, Address = ip.ToString() });
                    }
                    break;
                }
                case MdnsClient.TypeAaaa:
                {
                    if (dataLength == 16)
                    {
                        var bytes = new byte[16];
                        Array.Copy(buffer, dataOffset, bytes, 0, 16);
                        records.Add(new MdnsRecord { Name = name, Type = type, Address = new IPAddress(bytes).ToString() });
                    }
                    break;
                }
            }

            offset = dataOffset + dataLength;
        }

        return records;
    }

    private static int ReadName(byte[] buffer, int offset, out string name)
    {
        var labels = new List<string>();
        var cursor = offset;
        var guard = 0;
        var jumped = false;
        var resultOffset = offset;

        while (cursor < buffer.Length && guard++ < 128)
        {
            var lengthByte = buffer[cursor];
            if (lengthByte == 0)
            {
                cursor++;
                if (!jumped)
                {
                    resultOffset = cursor;
                }
                break;
            }
            if ((lengthByte & 0xC0) == 0xC0)
            {
                // 压缩指针：低 14 位是偏移
                if (cursor + 1 >= buffer.Length)
                {
                    break;
                }
                var pointer = ((lengthByte & 0x3F) << 8) | buffer[cursor + 1];
                if (!jumped)
                {
                    resultOffset = cursor + 2;
                    jumped = true;
                }
                cursor = pointer;
                continue;
            }
            cursor++;
            if (cursor + lengthByte > buffer.Length)
            {
                break;
            }
            labels.Add(Encoding.UTF8.GetString(buffer, cursor, lengthByte));
            cursor += lengthByte;
            if (!jumped)
            {
                resultOffset = cursor;
            }
        }

        name = string.Join(".", labels);
        return resultOffset;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        offset + 1 < buffer.Length ? (ushort)((buffer[offset] << 8) | buffer[offset + 1]) : (ushort)0;
}
