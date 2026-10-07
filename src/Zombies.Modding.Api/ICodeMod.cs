namespace Zombies.Modding.Api;

/// <summary>
/// The entry point of a Code mod. The game creates one instance of every public, non-abstract class that implements this
/// interface in the assemblies the mod's manifest lists, then calls <see cref="Register"/> once, before the world starts.
/// A Code mod is trusted: its code runs with the same rights as the game (ADR 0004), so the player is warned when installing it.
/// </summary>
public interface ICodeMod
{
    /// <summary>Adds what this mod brings to the game. Throwing stops the mods from loading, with the exception reported.</summary>
    void Register(IModRegistry registry);
}

/// <summary>
/// What a Code mod may add to the game. Everything is named by a Content ID in the mod's own namespace, such as
/// <c>mymod:trait/screamer</c> or <c>mymod:message/shout</c>; a name in another namespace or registered twice is refused
/// with an <see cref="ArgumentException"/>.
/// </summary>
public interface IModRegistry
{
    /// <summary>The id of the mod registering, which is the namespace of everything it registers.</summary>
    string ModId { get; }

    /// <summary>
    /// Adds a Trait that Zombie types in any mod can list by <paramref name="traitId"/> (kind <c>trait</c>). When a zombie of
    /// such a type spawns, <paramref name="setup"/> adds the Trait's components to it, reading the numbers the Zombie type gave.
    /// </summary>
    void RegisterTrait(string traitId, TraitSetup setup);

    /// <summary>
    /// Adds a message that clients send to the Server (kind <c>message</c>). The Server reads it, runs
    /// <paramref name="handler"/>, and tells the client when the handler refused it. Its number on the wire is assigned
    /// when the mods load, so the mod never picks one. A client-only mod cannot register a message, because the Server
    /// never loads it.
    /// </summary>
    void RegisterMessage<TMessage>(ModMessageHandler<TMessage> handler)
        where TMessage : IModMessage<TMessage>;
}
