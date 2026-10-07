using System.Numerics;
using System.Runtime.InteropServices;

namespace Zombies.Engine.Ai;

public enum SoundKind
{
    /// <summary>A noise any creature that hears it turns toward, such as a gunshot or footsteps.</summary>
    Noise,

    /// <summary>A noise creatures make among themselves, such as a zombie hitting a door. It draws only those with nothing better to do.</summary>
    Commotion,
}

/// <summary>A sound: where it was made, how far it carries in meters, what made it (an entity id, or 0), and what kind it is.</summary>
public readonly record struct SoundEvent(Vector3 Position, float Radius, uint Source, SoundKind Kind = SoundKind.Noise);

/// <summary>Something that can be seen this tick, such as a living player, with the point a watcher must see to notice it.</summary>
public readonly record struct SightTarget(uint Entity, Vector3 Eye);

/// <summary>What a creature's senses allow: how far it sees and hears, and how wide it looks.</summary>
public readonly record struct Senses(float SightMeters, float HearingMeters, float FieldOfViewCos, float NoticeMeters);

/// <summary>
/// Where creatures learn of the world through sight and sound. Anything can <see cref="Emit"/> a sound, such as a gunshot, footsteps,
/// or a door being hit; it is heard during the next tick and then forgotten. Whatever can be seen is published each tick as a
/// <see cref="SightTarget"/>. A creature hears a sound within both the sound's reach and its own hearing, and sees a target within its
/// sight, inside its field of view (or close enough to notice regardless), with nothing solid between them.
/// </summary>
public sealed class PerceptionBus
{
    private readonly Navigation _navigation;
    private List<SoundEvent> _incoming = new(64);
    private List<SoundEvent> _current = new(64);
    private readonly List<SightTarget> _visible = new(16);

    public PerceptionBus(Navigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _navigation = navigation;
    }

    /// <summary>The sounds that can be heard this tick.</summary>
    public ReadOnlySpan<SoundEvent> Sounds => CollectionsMarshal.AsSpan(_current);

    /// <summary>What can be seen this tick.</summary>
    public ReadOnlySpan<SightTarget> Visible => CollectionsMarshal.AsSpan(_visible);

    /// <summary>Makes a noise that creatures hear during the next tick.</summary>
    public void Emit(Vector3 position, float radius, uint source = 0, SoundKind kind = SoundKind.Noise)
    {
        if (radius > 0 && float.IsFinite(radius) && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
        {
            _incoming.Add(new SoundEvent(position, radius, source, kind));
        }
    }

    /// <summary>Starts a tick: the sounds made since the last one become audible, and nothing is visible until published again.</summary>
    public void BeginTick()
    {
        (_current, _incoming) = (_incoming, _current);
        _incoming.Clear();
        _visible.Clear();
    }

    public void Publish(SightTarget target) => _visible.Add(target);

    /// <summary>
    /// The sound this listener hears best, the one it is deepest within the reach of, leaving out <see cref="SoundKind.Commotion"/>
    /// unless <paramref name="includeCommotion"/>. False when it hears none.
    /// </summary>
    public bool TryHear(Vector3 listener, in Senses senses, bool includeCommotion, out SoundEvent heard)
    {
        heard = default;
        var best = float.NegativeInfinity;
        foreach (var sound in CollectionsMarshal.AsSpan(_current))
        {
            if (sound.Kind == SoundKind.Commotion && !includeCommotion)
            {
                continue;
            }

            var reach = MathF.Min(sound.Radius, senses.HearingMeters);
            var margin = reach - Vector3.Distance(listener, sound.Position);
            if (margin >= 0 && margin > best)
            {
                best = margin;
                heard = sound;
            }
        }

        return best >= 0;
    }

    /// <summary>The nearest target a watcher at <paramref name="eye"/>, facing <paramref name="yaw"/>, can see. False when it sees none.</summary>
    public bool TrySee(Vector3 eye, float yaw, in Senses senses, out SightTarget seen)
    {
        seen = default;
        var bestDistance = float.PositiveInfinity;
        var facing = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        foreach (var target in CollectionsMarshal.AsSpan(_visible))
        {
            var offset = target.Eye - eye;
            var distance = offset.Length();
            if (distance > senses.SightMeters || distance >= bestDistance)
            {
                continue;
            }

            if (distance > senses.NoticeMeters)
            {
                var flat = new Vector3(offset.X, 0, offset.Z);
                var flatLength = flat.Length();
                if (flatLength > 1e-4f && Vector3.Dot(flat / flatLength, facing) < senses.FieldOfViewCos)
                {
                    continue;
                }
            }

            if (_navigation.HasLineOfSight(eye, target.Eye))
            {
                bestDistance = distance;
                seen = target;
            }
        }

        return bestDistance < float.PositiveInfinity;
    }
}
