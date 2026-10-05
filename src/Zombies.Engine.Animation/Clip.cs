using System.Numerics;
using System.Text.Json;

namespace Zombies.Engine.Animation;

/// <summary>
/// One keyframe of a bone: how far it is moved and turned from its rest pose at a moment in the clip. Authored as a rotation in
/// degrees and an offset in voxels, and held here as a quaternion and meters.
/// </summary>
public sealed record Keyframe(float Time, Vector3 OffsetMeters, Quaternion Rotation);

/// <summary>The keyframes of one bone in a clip, in time order.</summary>
public sealed record BoneTrack(string Bone, IReadOnlyList<Keyframe> Keys)
{
    /// <summary>The bone's change from rest at <paramref name="time"/>, blending between the keyframes either side of it.</summary>
    public BoneTransform Sample(float time, bool loop, float duration)
    {
        if (Keys.Count == 1)
        {
            return At(Keys[0]);
        }

        time = loop ? Wrap(time, duration) : Math.Clamp(time, 0f, duration);
        var next = 0;
        while (next < Keys.Count && Keys[next].Time <= time)
        {
            next++;
        }

        Keyframe previous;
        Keyframe following;
        float previousTime;
        float followingTime;
        if (next == 0)
        {
            // Before the first key: held for a clip that plays once, and blended from the last key of the previous lap in a loop.
            if (!loop)
            {
                return At(Keys[0]);
            }

            previous = Keys[^1];
            following = Keys[0];
            previousTime = previous.Time - duration;
            followingTime = following.Time;
        }
        else if (next == Keys.Count)
        {
            if (!loop)
            {
                return At(Keys[^1]);
            }

            previous = Keys[^1];
            following = Keys[0];
            previousTime = previous.Time;
            followingTime = following.Time + duration;
        }
        else
        {
            previous = Keys[next - 1];
            following = Keys[next];
            previousTime = previous.Time;
            followingTime = following.Time;
        }

        var span = followingTime - previousTime;
        if (span <= 1e-6f)
        {
            return At(following);
        }

        return BoneTransform.Lerp(At(previous), At(following), Math.Clamp((time - previousTime) / span, 0f, 1f));
    }

    private static BoneTransform At(Keyframe key) => new(key.OffsetMeters, key.Rotation);

    private static float Wrap(float time, float duration) => time - (duration * MathF.Floor(time / duration));
}

/// <summary>
/// A named keyframe animation, such as walk or reload. Keyframes are changes from the rest pose, so one clip fits any
/// skeleton that has the bones it names. A looping clip blends its last keyframe back into its first.
/// </summary>
public sealed class Clip
{
    public const int MaxTracks = 128;

    public const int MaxKeysPerTrack = 256;

    public Clip(string name, float duration, bool loop, IEnumerable<BoneTrack> tracks)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!float.IsFinite(duration) || duration <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A clip must last longer than zero seconds.");
        }

        Name = name;
        Duration = duration;
        Loop = loop;
        Tracks = [.. tracks];
    }

    public string Name { get; }

    /// <summary>How long one play of the clip takes, in seconds.</summary>
    public float Duration { get; }

    public bool Loop { get; }

    public IReadOnlyList<BoneTrack> Tracks { get; }

    internal static bool TryParse(JsonElement element, string path, out Clip clip, out string error)
    {
        clip = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{path} must be an object.";
            return false;
        }

        if (!RigJson.TryString(element, "name", path, out var name, out error)
            || !RigJson.TryRequiredNumber(element, "duration", path, 0.01f, 600f, out var duration, out error)
            || !RigJson.TryBool(element, "loop", path, false, out var loop, out error)
            || !RigJson.TryArray(element, "tracks", path, required: true, MaxTracks, out var trackArray, out error))
        {
            return false;
        }

        var tracks = new List<BoneTrack>();
        var bones = new HashSet<string>(StringComparer.Ordinal);
        var trackIndex = 0;
        foreach (var trackElement in trackArray.EnumerateArray())
        {
            var trackPath = $"{path}.tracks[{trackIndex++}]";
            if (trackElement.ValueKind != JsonValueKind.Object)
            {
                error = $"{trackPath} must be an object.";
                return false;
            }

            if (!RigJson.TryString(trackElement, "bone", trackPath, out var bone, out error)
                || !RigJson.TryArray(trackElement, "keys", trackPath, required: true, MaxKeysPerTrack, out var keyArray, out error))
            {
                return false;
            }

            if (!bones.Add(bone))
            {
                error = $"{trackPath}: the clip '{name}' has two tracks for '{bone}'.";
                return false;
            }

            if (!TryKeys(keyArray, trackPath, duration, out var keys, out error))
            {
                return false;
            }

            tracks.Add(new BoneTrack(bone, keys));
        }

        clip = new Clip(name, duration, loop, tracks);
        return true;
    }

    private static bool TryKeys(JsonElement array, string path, float duration, out List<Keyframe> keys, out string error)
    {
        keys = [];
        error = string.Empty;
        if (array.GetArrayLength() == 0)
        {
            error = $"{path} needs at least one key.";
            return false;
        }

        var index = 0;
        var previous = -1f;
        foreach (var element in array.EnumerateArray())
        {
            var keyPath = $"{path}.keys[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"{keyPath} must be an object.";
                return false;
            }

            if (!RigJson.TryNumber(element, "time", keyPath, 0f, 0f, duration, out var time, out error)
                || !RigJson.TryVector(element, "rotation", keyPath, Vector3.Zero, out var rotation, out error)
                || !RigJson.TryVector(element, "offset", keyPath, Vector3.Zero, out var offset, out error))
            {
                return false;
            }

            if (time <= previous)
            {
                error = $"{keyPath}: key times must increase.";
                return false;
            }

            previous = time;
            keys.Add(new Keyframe(time, offset * VoxelScale.VoxelMeters, BoneTransform.FromEulerDegrees(rotation)));
        }

        return true;
    }
}

/// <summary>
/// The clips of one rig, by name, read from JSON as <c>{ "clips": [ { "name", "duration", "loop", "tracks": [ { "bone", "keys": [ { "time", "rotation", "offset" } ] } ] } ] }</c>.
/// A clip can have a crawl variant named like it with <see cref="CrawlSuffix"/>, which <see cref="Resolve"/> picks for a
/// character that is missing a leg.
/// </summary>
public sealed class ClipSet
{
    public const string CrawlSuffix = "_crawl";

    public const int MaxClips = 256;

    private readonly Dictionary<string, Clip> _clips;

    public ClipSet(IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        _clips = new Dictionary<string, Clip>(StringComparer.Ordinal);
        foreach (var clip in clips)
        {
            if (!_clips.TryAdd(clip.Name, clip))
            {
                throw new ArgumentException($"The clip '{clip.Name}' is listed twice.", nameof(clips));
            }
        }
    }

    public static ClipSet Empty { get; } = new([]);

    public IReadOnlyCollection<Clip> Clips => _clips.Values;

    public int Count => _clips.Count;

    public Clip? Get(string name) => _clips.GetValueOrDefault(name);

    /// <summary>
    /// The clip to play for <paramref name="name"/> given what a character is missing: the crawl variant when a leg is missing
    /// and the set has one, otherwise the clip itself, otherwise null.
    /// </summary>
    public Clip? Resolve(string name, MissingPartSet missing)
    {
        if (missing.IsCrawling() && _clips.TryGetValue(name + CrawlSuffix, out var crawl))
        {
            return crawl;
        }

        return _clips.GetValueOrDefault(name);
    }

    public static bool TryParse(string json, out ClipSet clips, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        clips = null!;
        if (!RigJson.TryParseRoot(json, "clip file", out var document, out error))
        {
            return false;
        }

        using (document)
        {
            if (!RigJson.TryArray(document.RootElement, "clips", "The clip file", required: true, MaxClips, out var array, out error))
            {
                return false;
            }

            var parsed = new List<Clip>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (!Clip.TryParse(element, $"clips[{index++}]", out var clip, out error))
                {
                    return false;
                }

                if (!names.Add(clip.Name))
                {
                    error = $"The clip '{clip.Name}' is listed twice.";
                    return false;
                }

                parsed.Add(clip);
            }

            clips = new ClipSet(parsed);
            return true;
        }
    }
}
