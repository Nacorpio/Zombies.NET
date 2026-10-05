namespace Zombies.Modding.Api;

/// <summary>
/// A message a Code mod defines: a Domain command a client sends to the Server. The type writes and reads its own payload;
/// the Server reads it, and fails it as malformed when it is short, too long, or holds a value no writer produces.
/// </summary>
public interface IModMessage<TSelf>
    where TSelf : IModMessage<TSelf>
{
    /// <summary>The Content ID of the message, in the mod's namespace and of kind <c>message</c>, such as <c>mymod:message/shout</c>.</summary>
    static abstract string Id { get; }

    void Write(ModMessageWriter writer);

    static abstract TSelf Read(ref ModMessageReader reader);
}

/// <summary>Who sent a message: the player's name and the id of their player entity, as snapshots show it.</summary>
public readonly record struct ModMessageSender(string PlayerName, uint PlayerEntity);

/// <summary>What the Server did with a message. A refused message must change nothing; its reason is sent back to the client.</summary>
public readonly record struct ModMessageResult(bool IsAccepted, string? Reason)
{
    public static ModMessageResult Accepted => new(true, null);

    public static ModMessageResult Refused(string reason) => new(false, reason);
}

/// <summary>Validates and applies one message on the Server. Validation must come first: a refused message changes nothing.</summary>
public delegate ModMessageResult ModMessageHandler<TMessage>(in TMessage message, ModMessageSender sender);
