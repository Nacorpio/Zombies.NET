using System.Buffers.Binary;
using System.Text;

namespace Zombies.Modding.Api;

/// <summary>Writes a message's payload, little-endian. It grows while warming up and is then reused without allocating.</summary>
public sealed class ModMessageWriter
{
    private byte[] _buffer;

    public ModMessageWriter(int capacity = 256) => _buffer = new byte[Math.Max(capacity, 16)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void WriteByte(byte value) => Take(1)[0] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

    /// <summary>Writes a UTF-8 string prefixed with its byte length as a 16-bit number.</summary>
    /// <exception cref="ArgumentException">The string is longer than <see cref="ModMessageReader.MaxStringBytes"/> bytes.</exception>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = Encoding.UTF8.GetByteCount(value);
        if (count > ModMessageReader.MaxStringBytes)
        {
            throw new ArgumentException($"A string may be at most {ModMessageReader.MaxStringBytes} bytes of UTF-8.", nameof(value));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)count);
        Encoding.UTF8.GetBytes(value, Take(count));
    }

    private Span<byte> Take(int count)
    {
        if (Length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        }

        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }
}
