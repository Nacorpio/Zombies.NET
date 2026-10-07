using System.Buffers.Binary;
using System.Text;

namespace Zombies.Engine.Net;

/// <summary>Thrown when a message is shorter than it claims or holds a value no writer produces. The sender is at fault.</summary>
public sealed class MalformedMessageException : Exception
{
    public MalformedMessageException()
    {
    }

    public MalformedMessageException(string message)
        : base(message)
    {
    }

    public MalformedMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads what <see cref="NetWriter"/> wrote. Every read is bounds checked, because a peer may send anything.</summary>
public ref struct NetReader(ReadOnlySpan<byte> data)
{
    private const int MaxStringBytes = 4096;

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new MalformedMessageException("A boolean must be 0 or 1."),
    };

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public uint ReadVarUInt()
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadByte();
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new MalformedMessageException("A variable-length integer is longer than 5 bytes.");
    }

    public string ReadString()
    {
        var count = ReadVarUInt();
        if (count > MaxStringBytes)
        {
            throw new MalformedMessageException($"A string of {count} bytes is longer than the {MaxStringBytes} allowed.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(Take((int)count));
        }
        catch (DecoderFallbackException ex)
        {
            throw new MalformedMessageException("A string is not valid UTF-8.", ex);
        }
    }

    /// <summary>Takes every byte left, such as a payload whose layout another reader knows.</summary>
    public ReadOnlySpan<byte> ReadToEnd() => Take(Remaining);

    /// <summary>Fails the message when bytes are left over, so a sender cannot smuggle data after a valid message.</summary>
    public readonly void EnsureEnd()
    {
        if (Remaining != 0)
        {
            throw new MalformedMessageException($"{Remaining} unexpected bytes after the end of the message.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
        {
            throw new MalformedMessageException("The message ended early.");
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }
}
