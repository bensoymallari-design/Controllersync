using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControllerSync.Core;

public sealed class FrameReader
{
    public const int MaxFrameBytes = 64 * 1024;

    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    public bool TryRead(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        var available = _end - _start;
        if (available < 4)
            return false;

        var length = Endian.ReadInt32(_buffer.AsSpan(_start, 4));
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} is not valid.");
        if (available < 4 + length)
            return false;

        frame = new byte[length];
        Array.Copy(_buffer, _start + 4, frame, 0, length);
        _start += 4 + length;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
        else if (_start > 8192)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        return true;
    }

    private void Ensure(int additional)
    {
        if (_buffer.Length - _end >= additional)
            return;

        var count = _end - _start;
        if (_buffer.Length - count >= additional)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, count);
            _start = 0;
            _end = count;
            return;
        }

        var next = Math.Max(_buffer.Length * 2, count + additional);
        var grown = new byte[next];
        if (count > 0)
            Buffer.BlockCopy(_buffer, _start, grown, 0, count);
        _buffer = grown;
        _start = 0;
        _end = count;
    }
}

internal static class Endian
{
    public static int ReadInt32(ReadOnlySpan<byte> data) =>
        data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);

    public static void WriteInt32(Span<byte> data, int value)
    {
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
        data[3] = (byte)(value >> 24);
    }
}

public static class ChannelGuard
{
    public static bool Matches(string expected, string? actual)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? ""));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(actual ?? ""));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public static class SyncCodec
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] EncodeFrame(SyncMessage message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length is <= 0 or > FrameReader.MaxFrameBytes)
            throw new InvalidDataException("Message is too large.");
        var frame = new byte[4 + body.Length];
        Endian.WriteInt32(frame, body.Length);
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> json, out SyncMessage? message, out string? error)
    {
        message = null;
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<SyncMessage>(json, Json);
            if (parsed == null || parsed.V != 1 || string.IsNullOrWhiteSpace(parsed.Id))
            {
                error = "Packet was ignored because it is not a ControllerSync message.";
                return false;
            }

            message = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "Packet was ignored because it was damaged.";
            return false;
        }
    }
}
