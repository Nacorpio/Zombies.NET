using System.Numerics;

namespace Zombies.Engine.Animation;

/// <summary>
/// What drives an animation this frame. <see cref="Speed"/> is how fast the character is moving along the ground, in meters per
/// second. <see cref="LookYaw"/> and <see cref="LookPitch"/> are where it is looking, in radians, relative to the way its body
/// faces: positive yaw is to the right and positive pitch is up, the same as the player's look angles.
/// </summary>
public readonly record struct AnimationInput(float Speed, float LookYaw, float LookPitch, MissingPartSet Missing)
{
    /// <summary>
    /// The input for a player: the ground speed from the velocity the movement model gives, ignoring falling and jumping, and the
    /// look relative to the way the body faces. The Server's player state and a client's prediction both feed it the same way.
    /// </summary>
    public static AnimationInput ForPlayer(Vector3 velocity, float bodyYaw, float aimYaw, float aimPitch, MissingPartSet missing) =>
        new(MathF.Sqrt((velocity.X * velocity.X) + (velocity.Z * velocity.Z)), RelativeYaw(aimYaw, bodyYaw), aimPitch, missing);

    /// <summary>The look yaw for aiming at <paramref name="aimYaw"/> with a body facing <paramref name="bodyYaw"/>, wrapped to the shorter way round.</summary>
    public static float RelativeYaw(float aimYaw, float bodyYaw)
    {
        var difference = (aimYaw - bodyYaw) % (2f * MathF.PI);
        if (difference > MathF.PI)
        {
            difference -= 2f * MathF.PI;
        }
        else if (difference < -MathF.PI)
        {
            difference += 2f * MathF.PI;
        }

        return difference;
    }
}

/// <summary>
/// Poses one skeleton frame by frame, the same way for a remote player, a zombie, and the first-person arms. Each frame it starts
/// from the rest pose and applies, in order: the keyframe clip for idle or moving (its crawl variant when a leg is missing), a
/// one-shot action clip such as reload, the procedural walk, and the procedural look-at. It touches no GPU and no clock, only the
/// time it is told has passed, so it can be tested by calling <see cref="Update"/> with numbers.
/// </summary>
public sealed class Animator
{
    /// <summary>The clip played while moving, which also has its phase set by how far the character has walked.</summary>
    public const string MoveClip = "walk";

    /// <summary>The clip played while standing still.</summary>
    public const string IdleClip = "idle";

    /// <summary>Below this speed in meters per second a character counts as standing still.</summary>
    public const float MovingThreshold = 0.1f;

    private readonly ClipSet _clips;
    private readonly Dictionary<Clip, int[]> _boundBones = [];
    private float _idleTime;
    private float _actionTime;

    public Animator(Skeleton skeleton, ClipSet clips)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clips);
        Skeleton = skeleton;
        _clips = clips;
        Pose = new RigPose(skeleton);
        foreach (var clip in clips.Clips)
        {
            var bones = new int[clip.Tracks.Count];
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i] = skeleton.IndexOf(clip.Tracks[i].Bone);
                if (bones[i] < 0)
                {
                    throw new ArgumentException($"The clip '{clip.Name}' animates '{clip.Tracks[i].Bone}', which is not a bone of '{skeleton.Id}'.", nameof(clips));
                }
            }

            _boundBones.Add(clip, bones);
        }
    }

    public Skeleton Skeleton { get; }

    /// <summary>The pose as of the last <see cref="Update"/>. The same object every time, filled in place.</summary>
    public RigPose Pose { get; }

    /// <summary>How far through a stride the character is, from 0 to 1. Advances with distance walked, not with time, so feet do not slide.</summary>
    public float WalkPhase { get; private set; }

    /// <summary>Whether the character is missing a leg and so plays crawl variants.</summary>
    public bool IsCrawling { get; private set; }

    /// <summary>The name of the idle or moving clip playing as of the last <see cref="Update"/>, which is the crawl variant's name when that was chosen, or null when the set has none for it.</summary>
    public string? LocomotionClip { get; private set; }

    /// <summary>The one-shot clip playing, or null.</summary>
    public string? ActionClip { get; private set; }

    /// <summary>
    /// Starts a one-shot clip such as fire or reload, replacing one already playing. It runs over the idle and moving clip for the
    /// bones it names, and ends by itself unless it loops. False when the set has no clip of that name.
    /// </summary>
    public bool PlayAction(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_clips.Get(name) is null)
        {
            return false;
        }

        ActionClip = name;
        _actionTime = 0f;
        return true;
    }

    public void StopAction() => ActionClip = null;

    public void Update(float seconds, in AnimationInput input)
    {
        if (!float.IsFinite(seconds) || seconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Time cannot run backwards.");
        }

        var speed = float.IsFinite(input.Speed) ? MathF.Max(0f, input.Speed) : 0f;
        var moving = speed > MovingThreshold;
        var walk = Skeleton.Walk;
        IsCrawling = input.Missing.IsCrawling();

        if (moving)
        {
            WalkPhase = Fraction(WalkPhase + (speed * seconds / walk.StrideMeters));
        }

        _idleTime += seconds;

        Pose.Reset(input.Missing);
        ApplyLocomotionClip(moving, input.Missing);
        ApplyAction(seconds);
        ApplyWalk(Math.Clamp(speed / walk.FullSpeed, 0f, 1f));
        ApplyLookAt(input.LookYaw, input.LookPitch);
        Pose.UpdateWorld();
    }

    private void ApplyLocomotionClip(bool moving, MissingPartSet missing)
    {
        var clip = _clips.Resolve(moving ? MoveClip : IdleClip, missing);
        LocomotionClip = clip?.Name;
        if (clip is null)
        {
            return;
        }

        // A walk clip is one stride long, so it is driven by the phase; an idle clip just runs.
        ApplyClip(clip, moving ? WalkPhase * clip.Duration : _idleTime);
    }

    private void ApplyAction(float seconds)
    {
        if (ActionClip is null || _clips.Get(ActionClip) is not { } clip)
        {
            return;
        }

        // A frame samples the clip at the time it has reached and then moves the time on, so the first frame is the clip's start.
        if (!clip.Loop && _actionTime >= clip.Duration)
        {
            ActionClip = null;
            return;
        }

        ApplyClip(clip, _actionTime);
        _actionTime += seconds;
    }

    private void ApplyClip(Clip clip, float time)
    {
        var bones = _boundBones[clip];
        for (var i = 0; i < bones.Length; i++)
        {
            var delta = clip.Tracks[i].Sample(time, clip.Loop, clip.Duration);
            var rest = Skeleton.Bones[bones[i]].Rest;
            Pose.SetLocal(bones[i], new BoneTransform(rest.Position + delta.Position, rest.Rotation * delta.Rotation));
        }
    }

    private void ApplyWalk(float intensity)
    {
        if (intensity <= 0f)
        {
            return;
        }

        foreach (var swing in Skeleton.Walk.Swings)
        {
            if ((swing.Upright && IsCrawling) || !Pose.IsVisible(swing.Bone))
            {
                continue;
            }

            var wave = MathF.Sin(2f * MathF.PI * ((WalkPhase * swing.Frequency) + swing.Phase)) * intensity;
            var local = Pose.Local(swing.Bone);
            Pose.SetLocal(
                swing.Bone,
                new BoneTransform(
                    local.Position + (swing.OffsetMeters * wave),
                    local.Rotation * BoneTransform.FromEulerDegrees(swing.RotationDegrees * wave)));
        }
    }

    private void ApplyLookAt(float yaw, float pitch)
    {
        if (Skeleton.LookAt.Count == 0 || !float.IsFinite(yaw) || !float.IsFinite(pitch))
        {
            return;
        }

        foreach (var link in Skeleton.LookAt)
        {
            if (!Pose.IsVisible(link.Bone))
            {
                continue;
            }

            var turn = Math.Clamp(yaw * link.YawWeight, -link.MaxYawRadians, link.MaxYawRadians);
            var tilt = Math.Clamp(pitch * link.PitchWeight, -link.MaxPitchRadians, link.MaxPitchRadians);

            // Positive yaw faces right, which is a turn about Y the other way round; positive pitch faces up, a turn about X.
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -turn) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, tilt);
            var local = Pose.Local(link.Bone);
            Pose.SetLocal(link.Bone, new BoneTransform(local.Position, local.Rotation * rotation));
        }
    }

    private static float Fraction(float value) => value - MathF.Floor(value);
}
