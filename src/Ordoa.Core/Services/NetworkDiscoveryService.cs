using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

public sealed record DiscoveredService(
    string Source,
    string Name,
    string Host,
    int Port,
    RemoteProtocol? Protocol,
    string Detail)
{
    public string DisplayTarget => Port > 0 ? $"{Host}:{Port}" : Host;
    public string ProtocolLabel => Protocol?.ToString().ToUpperInvariant() ?? "—";
    public bool CanUse => Protocol is not null && Port > 0 && !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Best-effort local network discovery. mDNS uses a small native DNS packet
/// implementation (no daemon requirement); WS-Discovery uses the standard
/// multicast SOAP probe. Failures are isolated so one unavailable transport
/// never hides results from the other.
/// </summary>
public sealed class NetworkDiscoveryService
{
    private static readonly (string Type, RemoteProtocol Protocol)[] MdnsServices =
    [
        ("_sftp-ssh._tcp.local", RemoteProtocol.Sftp),
        ("_smb._tcp.local", RemoteProtocol.Smb),
        ("_ftp._tcp.local", RemoteProtocol.Ftp),
        ("_webdav._tcp.local", RemoteProtocol.WebDav),
        ("_webdavs._tcp.local", RemoteProtocol.WebDav),
        ("_http._tcp.local", RemoteProtocol.WebDav),
    ];

    public async Task<IReadOnlyList<DiscoveredService>> DiscoverAsync(TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var duration = timeout ?? TimeSpan.FromSeconds(2.5);
        var results = new List<DiscoveredService>();
        var mdns = await MdnsAsync(duration, cancellationToken).ConfigureAwait(false);
        results.AddRange(mdns);
        try
        {
            results.AddRange(await WsDiscoveryAsync(duration, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // WS-Discovery is optional.
        }
        catch
        {
            // A firewall may block multicast; keep mDNS results.
        }

        return results
            .GroupBy(item => $"{item.Source}|{item.Name}|{item.Host}|{item.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<DiscoveredService>> MdnsAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse("224.0.0.251"), IPAddress.Any));
        }
        catch
        {
            // Some systems reserve 5353. Unicast responses still work for many
            // responders, so keep the socket if the bind succeeded.
        }

        var query = BuildQuery(MdnsServices.Select(item => item.Type));
        var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        try
        {
            await socket.SendToAsync(query, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }

        var records = new List<MdnsRecord>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[9000];
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token).ConfigureAwait(false);
                var packet = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                records.AddRange(ParseRecords(packet));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Normal timeout.
        }
        catch (SocketException)
        {
            // Firewall or interface down.
        }
        finally
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                    new MulticastOption(IPAddress.Parse("224.0.0.251"), IPAddress.Any));
            }
            catch { /* ignore */ }
        }

        return PairMdns(records);
    }

    private static List<DiscoveredService> PairMdns(IReadOnlyList<MdnsRecord> records)
    {
        var result = new List<DiscoveredService>();
        var serviceTypes = MdnsServices.ToDictionary(item => item.Type.TrimEnd('.'), item => item.Protocol,
            StringComparer.OrdinalIgnoreCase);
        foreach (var ptr in records.Where(record => record.Type == 12))
        {
            var serviceType = ptr.Name.TrimEnd('.');
            if (!serviceTypes.TryGetValue(serviceType, out var protocol))
                continue;
            var instance = ptr.Target.TrimEnd('.');
            var srv = records.FirstOrDefault(record => record.Type == 33 &&
                                                       string.Equals(record.Name.TrimEnd('.'), instance,
                                                           StringComparison.OrdinalIgnoreCase));
            if (srv is null || srv.Port <= 0)
                continue;

            var host = srv.Target.TrimEnd('.');
            var address = records.FirstOrDefault(record => record.Type == 1 &&
                string.Equals(record.Name.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))?.Address;
            var displayHost = string.IsNullOrWhiteSpace(address) ? host : address;
            var name = instance.Split('.')[0].Replace("\\032", " ");
            var txt = records.FirstOrDefault(record => record.Type == 16 &&
                string.Equals(record.Name.TrimEnd('.'), instance, StringComparison.OrdinalIgnoreCase));
            var details = txt is null ? string.Empty : string.Join(" · ", txt.TextValues);
            result.Add(new DiscoveredService("mDNS", name, displayHost, srv.Port, protocol, details));
        }
        return result;
    }

    private static async Task<IReadOnlyList<DiscoveredService>> WsDiscoveryAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var messageId = "urn:uuid:" + Guid.NewGuid();
        var probe = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl"
                        xmlns:wsdp="http://schemas.xmlsoap.org/ws/2006/02/devprof">
              <s:Header>
                <a:Action s:mustUnderstand="1">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
                <a:MessageID>{messageId}</a:MessageID>
                <a:To s:mustUnderstand="1">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
              </s:Header>
              <s:Body><d:Probe><d:Types>wsdp:Device dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body>
            </s:Envelope>
            """;
        var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
        var bytes = Encoding.UTF8.GetBytes(probe);
        await socket.SendToAsync(bytes, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);

        var results = new List<DiscoveredService>();
        var buffer = new byte[64 * 1024];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token).ConfigureAwait(false);
                var xmlText = Encoding.UTF8.GetString(buffer, 0, received.ReceivedBytes);
                var xml = XDocument.Parse(xmlText);
                foreach (var element in xml.Descendants())
                {
                    if (!element.Name.LocalName.Equals("XAddrs", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var address in element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                            continue;
                        results.Add(new DiscoveredService("WS-Discovery", uri.Host, uri.Host, uri.Port,
                            null, address));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Normal timeout.
        }
        return results;
    }

    private static byte[] BuildQuery(IEnumerable<string> names)
    {
        var body = new List<byte>();
        var count = 0;
        foreach (var name in names)
        {
            WriteName(body, name);
            body.Add(0);
            body.Add(12); // PTR
            body.Add(0);
            body.Add(1); // IN
            count++;
        }

        var header = new byte[12];
        header[0] = 0x12;
        header[1] = 0x34;
        header[4] = (byte)(count >> 8);
        header[5] = (byte)count;
        return header.Concat(body).ToArray();
    }

    private static void WriteName(List<byte> target, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            target.Add((byte)bytes.Length);
            target.AddRange(bytes);
        }
    }

    private static IReadOnlyList<MdnsRecord> ParseRecords(byte[] packet)
    {
        if (packet.Length < 12)
            return [];
        var reader = new DnsReader(packet);
        var questions = ReadUInt16(packet, 4);
        var answers = ReadUInt16(packet, 6);
        var authority = ReadUInt16(packet, 8);
        var additional = ReadUInt16(packet, 10);
        reader.Position = 12;
        for (var i = 0; i < questions; i++)
        {
            reader.ReadName();
            reader.Position += 4;
        }

        var records = new List<MdnsRecord>();
        var total = answers + authority + additional;
        for (var i = 0; i < total; i++)
        {
            var name = reader.ReadName();
            if (reader.Position + 10 > packet.Length)
                break;
            var type = reader.ReadUInt16();
            var recordClass = reader.ReadUInt16();
            reader.Position += 4; // TTL
            var length = reader.ReadUInt16();
            var end = reader.Position + length;
            if (end > packet.Length)
                break;
            var start = reader.Position;
            switch (type)
            {
                case 1 when length == 4:
                    records.Add(new MdnsRecord(name, type, string.Empty, 0,
                        $"{packet[start]}.{packet[start + 1]}.{packet[start + 2]}.{packet[start + 3]}", []));
                    break;
                case 12:
                    reader.Position = start;
                    records.Add(new MdnsRecord(name, type, reader.ReadName(), 0, string.Empty, []));
                    break;
                case 16:
                    var values = new List<string>();
                    while (reader.Position < end)
                    {
                        var len = packet[reader.Position++];
                        if (reader.Position + len > end)
                            break;
                        values.Add(Encoding.UTF8.GetString(packet, reader.Position, len));
                        reader.Position += len;
                    }
                    records.Add(new MdnsRecord(name, type, string.Empty, 0, string.Empty, values));
                    break;
                case 33 when length >= 6:
                    reader.Position = start + 4;
                    var port = reader.ReadUInt16();
                    var target = reader.ReadName();
                    records.Add(new MdnsRecord(name, type, target, port, string.Empty, []));
                    break;
            }
            reader.Position = end;
        }
        return records;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private sealed record MdnsRecord(string Name, ushort Type, string Target, int Port, string Address,
        IReadOnlyList<string> TextValues);

    private sealed class DnsReader(byte[] data)
    {
        public int Position { get; set; }

        public string ReadName()
        {
            var labels = new List<string>();
            var next = -1;
            var position = Position;
            var jumps = 0;
            while (position < data.Length && jumps++ < 24)
            {
                var length = data[position];
                if (length == 0)
                {
                    position++;
                    if (next < 0)
                        next = position;
                    break;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    if (position + 1 >= data.Length)
                        break;
                    var pointer = ((length & 0x3F) << 8) | data[position + 1];
                    if (next < 0)
                        next = position + 2;
                    position = pointer;
                    continue;
                }
                if (position + 1 + length > data.Length)
                    break;
                labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
                position += length + 1;
            }
            Position = next >= 0 ? next : position;
            return string.Join('.', labels);
        }

        public ushort ReadUInt16()
        {
            if (Position + 2 > data.Length)
            {
                Position = data.Length;
                return 0;
            }
            var value = (ushort)((data[Position] << 8) | data[Position + 1]);
            Position += 2;
            return value;
        }
    }
}
