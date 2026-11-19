using System.Buffers.Binary;
using System.Text;

namespace AudioTastic.Casting.Chromecast;

/// <summary>
/// CN-01: one Cast v2 message (the CastMessage protobuf of the Cast protocol): protocol version (always 0), source id,
/// destination id, namespace and a UTF-8 JSON payload. Encoded by hand: six fields do not need a protobuf library.
/// On the wire each message is preceded by its length as a 4-byte big-endian number.
/// </summary>
public sealed record CastMessage(string SourceId, string DestinationId, string Namespace, string Payload)
{
    /// <summary>The protobuf bytes (without the length prefix).</summary>
    public byte[] Encode()
    {
        var output = new List<byte>(64 + Payload.Length);
        WriteVarintField(output, 1, 0); // protocol_version = CASTV2_1_0
        WriteStringField(output, 2, SourceId);
        WriteStringField(output, 3, DestinationId);
        WriteStringField(output, 4, Namespace);
        WriteVarintField(output, 5, 0); // payload_type = STRING
        WriteStringField(output, 6, Payload);
        return [.. output];
    }

    /// <summary>The message with its 4-byte length prefix, ready to send.</summary>
    public byte[] Frame()
    {
        var body = Encode();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Reads the protobuf bytes of a message; unknown fields (binary payloads) are skipped.</summary>
    public static CastMessage Decode(ReadOnlySpan<byte> data)
    {
        string source = "", destination = "", ns = "", payload = "";
        var i = 0;
        while (i < data.Length)
        {
            var key = ReadVarint(data, ref i);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0:
                    ReadVarint(data, ref i);
                    break;
                case 2:
                    var length = (int)ReadVarint(data, ref i);
                    if (length < 0 || i + length > data.Length)
                    {
                        throw new InvalidDataException("Cast message field runs past the end.");
                    }
                    var text = Encoding.UTF8.GetString(data.Slice(i, length));
                    i += length;
                    switch (field)
                    {
                        case 2: source = text; break;
                        case 3: destination = text; break;
                        case 4: ns = text; break;
                        case 6: payload = text; break;
                    }
                    break;
                case 1:
                    i += 8;
                    break;
                case 5:
                    i += 4;
                    break;
                default:
                    throw new InvalidDataException("Unknown protobuf wire type in a Cast message.");
            }
        }
        return new CastMessage(source, destination, ns, payload);
    }

    private static void WriteVarintField(List<byte> output, int field, ulong value)
    {
        WriteVarint(output, (ulong)(field << 3));
        WriteVarint(output, value);
    }

    private static void WriteStringField(List<byte> output, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(output, (ulong)((field << 3) | 2));
        WriteVarint(output, (ulong)bytes.Length);
        output.AddRange(bytes);
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }
        throw new InvalidDataException("Varint too long in a Cast message.");
    }
}
