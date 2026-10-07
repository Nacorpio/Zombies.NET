using Zombies.Modding.Api;

namespace SampleCodeMod;

/// <summary>
/// The component the Screamer Trait gives a zombie: how loud it screams, from 0 to 1. A component is a plain struct the mod
/// owns; the game stores it on the zombie without knowing its type.
/// </summary>
public readonly record struct Screamer(double Volume);

/// <summary>
/// A player shouting, with a loudness from 0 to 1. Clients send it; the Server checks it. On the wire it is one 32-bit float.
/// The mod never chooses its number on the wire: the game assigns one from the sorted list of mods the Server runs.
/// </summary>
public readonly record struct Shout(float Loudness) : IModMessage<Shout>
{
    public static string Id => "sample_code:message/shout";

    public static Shout Read(ref ModMessageReader reader) => new(reader.ReadSingle());

    public void Write(ModMessageWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteSingle(Loudness);
    }
}

/// <summary>
/// The sample Code mod: a Zombie Trait and a message, added only through the public modding API. Its manifest declares it
/// for both sides, so it loads on the Server and on every client; its <c>sample_code:zombie/screamer</c> type lists the Trait.
/// </summary>
public sealed class SampleCodeMod : ICodeMod
{
    public const string ScreamerTrait = "sample_code:trait/screamer";

    /// <summary>How loud a screamer is when its Zombie type does not say.</summary>
    public const double DefaultVolume = 1.0;

    public void Register(IModRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterTrait(ScreamerTrait, (zombie, values) =>
            zombie.Add(new Screamer(Math.Clamp(values.TryGetValue("volume", out var volume) ? volume : DefaultVolume, 0, 1))));
        registry.RegisterMessage<Shout>(HandleShout);
    }

    private static ModMessageResult HandleShout(in Shout shout, ModMessageSender sender) =>
        float.IsFinite(shout.Loudness) && shout.Loudness is >= 0f and <= 1f
            ? ModMessageResult.Accepted
            : ModMessageResult.Refused($"{sender.PlayerName} shouted at loudness {shout.Loudness}; a shout's loudness must be between 0 and 1.");
}
