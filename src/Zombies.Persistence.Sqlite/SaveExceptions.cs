namespace Zombies.Persistence.Sqlite;

/// <summary>A save file could not be used. The message says why in terms a player or server owner can act on.</summary>
public class SaveException : Exception
{
    public SaveException(string message)
        : base(message)
    {
    }

    public SaveException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The save was written by a newer build than this one, so this build must not touch it.</summary>
public sealed class SaveTooNewException(int found, int supported)
    : SaveException($"The save has schema version {found}, but this build only understands up to {supported}. Use a newer build.")
{
    public int Found { get; } = found;

    public int Supported { get; } = supported;
}

/// <summary>Something in the save is not a state the game can be in, such as a negative count or an unknown part.</summary>
public sealed class SaveCorruptException(string message, Exception? inner = null)
    : SaveException(message, inner ?? new InvalidDataException(message));
