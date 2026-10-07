using System.Buffers.Binary;
using System.Text;

namespace Zombies.Modding.Api;

/// <summary>A message's payload could not be read: it is shorter than it claims or holds a value no writer produces. The sender is at fault.</summary>
public sealed class MalformedModMessageException : Exception
{
    public MalformedModMessageException()
    {
    }

    public MalformedModMessageException(string message)
        : base(message)
    {
    }

    public MalformedModMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads what <see cref="ModMessageWriter"/> wrote. Every read is bounds checked, because a client may send anything.</summary>
public ref struct ModMessageReader(ReadOnlySpan<byte> data)
{
    /// <summary>The longest string a message may carry, in bytes of UTF-8.</summary>
    public const int MaxStringBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new MalformedModMessageException("A boolean must be 0 or 1."),
    };

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public string ReadString()
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        if (count > MaxStringBytes)
        {
            throw new MalformedModMessageException($"A string of {count} bytes is longer than the {MaxStringBytes} allowed.");
        }

        try
        {
            return StrictUtf8.GetString(Take(count));
        }
        catch (DecoderFallbackException ex)
        {
            throw new MalformedModMessageException("A string is not valid UTF-8.", ex);
        }
    }

    /// <summary>Fails the message when bytes are left over, so a sender cannot smuggle data after a valid message.</summary>
    public readonly void EnsureEnd()
    {
        if (Remaining != 0)
        {
            throw new MalformedModMessageException($"{Remaining} unexpected bytes after the end of the message.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
        {
            throw new MalformedModMessageException("The message ended early.");
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }
}
