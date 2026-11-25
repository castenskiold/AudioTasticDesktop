using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AudioTastic.Casting.Discovery;

/// <summary>A DNS resource record from an mDNS answer (only the types discovery needs are decoded).</summary>
public sealed record DnsRecord(string Name, ushort Type, TimeSpan Ttl)
{
    /// <summary>PTR: the instance name it points to.</summary>
    public string? PointsTo { get; init; }

    /// <summary>SRV: target host and port.</summary>
    public string? Target { get; init; }

    public int Port { get; init; }

    /// <summary>TXT: key=value pairs (keys lower case).</summary>
    public IReadOnlyDictionary<string, string>? Text { get; init; }

    /// <summary>A / AAAA: the address.</summary>
    public IPAddress? Address { get; init; }
}

/// <summary>
/// CN-05: the small part of DNS (RFC 1035) that mDNS service discovery (RFC 6762/6763) needs: building a PTR query and
/// reading the PTR, SRV, TXT, A and AAAA records of the answers, with name compression. Own code: no library needed.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;
    public const ushort TypeSrv = 33;

    /// <summary>
    /// A query for <paramref name="service"/> (e.g. "_googlecast._tcp.local") asking for PTR records, with the
    /// "unicast response" bit set, so answers come straight back to the asking socket (RFC 6762 §5.4).
    /// </summary>
    public static byte[] PtrQuery(string service)
    {
        var name = EncodeName(service);
        var message = new byte[12 + name.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1); // one question
        name.CopyTo(message, 12);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12 + name.Length), TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12 + name.Length + 2), 0x8001); // QU bit + class IN
        return message;
    }

    /// <summary>All answer, authority and additional records of a response; empty for queries or broken packets.</summary>
    public static IReadOnlyList<DnsRecord> Parse(ReadOnlySpan<byte> message)
    {
        var records = new List<DnsRecord>();
        if (message.Length < 12 || (message[2] & 0x80) == 0)
        {
            return records; // too short, or a query rather than a response
        }
        try
        {
            var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
            var count = BinaryPrimitives.ReadUInt16BigEndian(message[6..]) + BinaryPrimitives.ReadUInt16BigEndian(message[8..])
                + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                ReadName(message, ref offset);
                offset += 4;
            }
            for (var i = 0; i < count; i++)
            {
                var name = ReadName(message, ref offset);
                var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
                var ttl = TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt32BigEndian(message[(offset + 4)..]));
                var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
                offset += 10;
                var data = offset;
                offset += length;
                if (offset > message.Length)
                {
                    break;
                }
                var record = new DnsRecord(name, type, ttl);
                switch (type)
                {
                    case TypePtr:
                        var p = data;
                        records.Add(record with { PointsTo = ReadName(message, ref p) });
                        break;
                    case TypeSrv:
                        var s = data + 6;
                        records.Add(record with { Port = BinaryPrimitives.ReadUInt16BigEndian(message[(data + 4)..]), Target = ReadName(message, ref s) });
                        break;
                    case TypeTxt:
                        records.Add(record with { Text = ReadText(message.Slice(data, length)) });
                        break;
                    case TypeA when length == 4:
                    case TypeAaaa when length == 16:
                        records.Add(record with { Address = new IPAddress(message.Slice(data, length)) });
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            // A truncated or malformed packet (any device on the network can send one; P14 fuzzing found names that run
            // past the end): keep what was read.
        }
        return records;
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>Reads a possibly compressed name at <paramref name="offset"/> and moves past it.</summary>
    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = message[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = position + 1;
                }
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }
                jumped = true;
                position = pointer;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
        }
        throw new InvalidDataException("DNS name with too many labels or a pointer loop.");
    }

    private static Dictionary<string, string> ReadText(ReadOnlySpan<byte> data)
    {
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i];
            var entry = Encoding.UTF8.GetString(data.Slice(i + 1, Math.Min(length, data.Length - i - 1)));
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                text[entry[..equals]] = entry[(equals + 1)..];
            }
            i += 1 + length;
        }
        return text;
    }
}
