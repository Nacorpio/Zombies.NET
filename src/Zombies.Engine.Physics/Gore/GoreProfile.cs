using Zombies.Engine.Core;

namespace Zombies.Engine.Physics.Gore;

/// <summary>
/// How much of each visual a <see cref="GoreLevel"/> draws. Off draws no blood and no severed parts, so a limb that is shot off just
/// goes; the zombie still collapses, because that is movement and not gore. This scales visuals only and is never given to the Server.
/// </summary>
public sealed record GoreProfile(int DecalsPerHit, int ParticlesPerHit, int DeathDecals, int DeathParticles, bool ShowSeveredParts)
{
    public static GoreProfile For(GoreLevel level) => level switch
    {
        GoreLevel.Off => new GoreProfile(0, 0, 0, 0, ShowSeveredParts: false),
        GoreLevel.Low => new GoreProfile(1, 6, 3, 16, ShowSeveredParts: true),
        GoreLevel.High => new GoreProfile(3, 24, 8, 64, ShowSeveredParts: true),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a gore level."),
    };
}
