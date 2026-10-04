using System.Buffers.Binary;
using System.Text;

namespace Zombies.Engine.Net;

/// <summary>A reusable little-endian message writer. It grows while warming up and is then reused without allocating.</summary>
public sealed class NetWriter
{
    private byte[] _buffer;

    public NetWriter(int capacity = 1024) => _buffer = new byte[capacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void WriteByte(byte value) => Take(1)[0] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);

    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

    /// <summary>Writes an unsigned integer in 1 to 5 bytes, 7 bits at a time.</summary>
    public void WriteVarUInt(uint value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes a UTF-8 string prefixed with its byte length.</summary>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt((uint)count);
        Encoding.UTF8.GetBytes(value, Take(count));
    }

    /// <summary>Reserves two bytes to fill in later with <see cref="PatchUInt16"/>, such as a count known only after the items are written.</summary>
    public int ReserveUInt16()
    {
        var at = Length;
        Take(2);
        return at;
    }

    public void PatchUInt16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(at, 2), value);

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
