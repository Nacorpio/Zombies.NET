using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;
using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

/// <summary>
/// What the player is asking for this tick. The client samples its keys into this and sends it; the Server applies the
/// same <see cref="PlayerMovement"/> to the same input, so both sides reach the same position.
/// </summary>
public readonly record struct PlayerInput(
    float Forward,
    float Strafe,
    float Yaw,
    float Pitch,
    bool Sprint,
    bool Crouch,
    bool LeanLeft,
    bool LeanRight,
    bool Jump)
{
    /// <summary>Clamps the axes and makes the angles finite, so a hostile client cannot send nonsense.</summary>
    public PlayerInput Sanitized() => this with
    {
        Forward = Math.Clamp(Forward, -1f, 1f),
        Strafe = Math.Clamp(Strafe, -1f, 1f),
        Yaw = float.IsFinite(Yaw) ? Yaw : 0f,
        Pitch = float.IsFinite(Pitch) ? Pitch : 0f,
    };

    public bool IsValid =>
        float.IsFinite(Forward) && float.IsFinite(Strafe) && float.IsFinite(Yaw) && float.IsFinite(Pitch)
        && Math.Abs(Forward) <= 1f && Math.Abs(Strafe) <= 1f;
}

/// <summary>
/// Where a player is and how they are moving. The Server owns this; a client predicts it and reconciles against the Server's copy.
/// <see cref="Stamina"/> runs from 0 to <see cref="PlayerMovement.FullStamina"/>; <see cref="Exhausted"/> keeps the player from
/// sprinting until it has recovered. <see cref="Noise"/> is how loud the player is this tick, in multiples of a walking step,
/// for systems that listen.
/// </summary>
public readonly record struct PlayerMoveState(
    Vector3 Position,
    Vector3 Velocity,
    float Yaw,
    float Pitch,
    bool Crouched,
    bool OnGround,
    float Stamina = PlayerMovement.FullStamina,
    bool Exhausted = false,
    float Noise = 0f)
{
    public static PlayerMoveState At(Vector3 position, float yaw = 0f) => new(position, Vector3.Zero, yaw, 0f, false, false);
}

/// <summary>
/// The one movement model, shared by the Server and by client prediction (ADR 0003). It is a pure function of the previous
/// state and one input, so replaying the same inputs always gives the same position. The step is fixed at
/// <see cref="Simulation.TickRateHz"/>, which is why the Server and the client agree.
/// </summary>
public static class PlayerMovement
{
    /// <summary>How tall the player stands, in blocks. The eye sits <see cref="EyeHeight"/> above the feet.</summary>
    public const float StandingHeight = 1.8f;

    public const float CrouchHeight = 1.2f;

    public const float EyeHeight = 1.62f;

    public const float CrouchEyeHeight = 1.02f;

    /// <summary>Half the width of the player's collision box.</summary>
    public const float Radius = 0.3f;

    /// <summary>Walking speed in blocks per second. A Movement mode's speed multiplies it.</summary>
    public const float WalkSpeed = 4.3f;

    /// <summary>A full pool of Stamina.</summary>
    public const float FullStamina = 1f;

    /// <summary>How much of the pool a mode with a Stamina multiplier of 1 drains per second while moving.</summary>
    public const float StaminaDrainPerSecond = 0.05f;

    /// <summary>How much of the pool comes back per second while the player rests, or moves in a mode that costs nothing.</summary>
    public const float StaminaRecoveryPerSecond = 0.15f;

    /// <summary>After running dry the player cannot sprint again until Stamina is back to this level.</summary>
    public const float ExhaustedUntil = 0.25f;

    /// <summary>Carried mass up to this many kilograms costs no extra Stamina.</summary>
    public const float EncumbranceThresholdKilograms = 20f;

    /// <summary>The Stamina multiplier each kilogram above <see cref="EncumbranceThresholdKilograms"/> adds to every mode.</summary>
    public const float StaminaPerOverweightKilogram = 0.025f;

    /// <summary>How fast the player reaches full speed, in blocks per second squared.</summary>
    public const float GroundAcceleration = 40f;

    public const float AirAcceleration = 8f;

    /// <summary>Gravity in blocks per second squared. Higher than real gravity so a jump feels snappy.</summary>
    public const float Gravity = 24f;

    public const float JumpSpeed = 7.2f;

    /// <summary>Fastest a player can fall, in blocks per second. Also bounds how far one tick can move a player.</summary>
    public const float MaxFallSpeed = 50f;

    /// <summary>The highest ledge the player walks up without jumping.</summary>
    public const float StepHeight = 0.6f;

    /// <summary>How far the player may lean sideways, in blocks.</summary>
    public const float LeanOffset = 0.35f;

    /// <summary>How fast the lean moves in and out, in blocks per second.</summary>
    public const float LeanSpeed = 2.5f;

    /// <summary>
    /// How far the player may move in one tick before the Server calls it a teleport. The Server runs the movement itself,
    /// so this only catches a bug; it is set above <see cref="MaxFallSpeed"/> so a fall is never mistaken for a cheat.
    /// </summary>
    public const float MaxStepPerTick = (MaxFallSpeed / Simulation.TickRateHz) + 0.1f;

    public static float StepSeconds => 1f / Simulation.TickRateHz;

    /// <summary>Eye height for a stance, which the camera uses.</summary>
    public static float EyeHeightFor(bool crouched) => crouched ? CrouchEyeHeight : EyeHeight;

    /// <summary>Height of the collision box for a stance.</summary>
    public static float HeightFor(bool crouched) => crouched ? CrouchHeight : StandingHeight;

    /// <summary>Where the eye is, given the feet position and the stance.</summary>
    public static Vector3 EyePosition(in PlayerMoveState state) => state.Position + new Vector3(0, EyeHeightFor(state.Crouched), 0);

    /// <summary>The Stat that the movement Limb score and other Modifiers change.</summary>
    public static readonly StatName MoveSpeed = new("move_speed");

    /// <summary>The Movement modes the game starts with, for a caller that has not loaded any: walk, a faster and louder sprint, and a slow, quiet crouch.</summary>
    public static MovementModes DefaultModes { get; } = new(
    [
        new MovementModeDefinition("base:movement_mode/walk", MovementTrigger.Walk, 1, 1, 0),
        new MovementModeDefinition("base:movement_mode/sprint", MovementTrigger.Sprint, 1.5, 2.5, 4),
        new MovementModeDefinition("base:movement_mode/crouch", MovementTrigger.Crouch, 0.4, 0.3, 0),
    ]);

    /// <summary>The Movement mode the input asks for. Crouching beats sprinting, and an exhausted player who asks to sprint walks.</summary>
    public static MovementModeDefinition ModeFor(in PlayerMoveState state, in PlayerInput input, MovementModes modes)
    {
        var sprint = input.Sprint && !state.Exhausted && state.Stamina > 0f;
        return modes.For(input.Crouch ? MovementTrigger.Crouch : sprint ? MovementTrigger.Sprint : MovementTrigger.Walk);
    }

    /// <summary>Horizontal speed a mode asks for, before acceleration, after the <paramref name="modifiers"/> on <see cref="MoveSpeed"/>.</summary>
    public static float TargetSpeed(MovementModeDefinition mode, ModifierSet? modifiers = null)
    {
        var speed = WalkSpeed * (float)mode.Speed;
        return modifiers is null ? speed : MathF.Max(0f, (float)modifiers.EffectiveValue(MoveSpeed, speed));
    }

    /// <summary>How much of the pool a moving player drains per second in a mode, carrying the given mass.</summary>
    public static float StaminaDrain(MovementModeDefinition mode, float carriedKilograms) =>
        StaminaDrainPerSecond * ((float)mode.Stamina + (MathF.Max(0f, carriedKilograms - EncumbranceThresholdKilograms) * StaminaPerOverweightKilogram));

    /// <summary>The horizontal direction the input asks for, in world space, already normalized.</summary>
    public static Vector3 WishDirection(in PlayerInput input)
    {
        var forward = new Vector3(MathF.Sin(input.Yaw), 0, -MathF.Cos(input.Yaw));
        var right = new Vector3(MathF.Cos(input.Yaw), 0, MathF.Sin(input.Yaw));
        var wish = (forward * input.Forward) + (right * input.Strafe);
        var length = wish.Length();
        return length > 1e-4f ? wish / length : Vector3.Zero;
    }

    /// <summary>
    /// One fixed step. <paramref name="collide"/> resolves the move against the world and reports whether the player ended
    /// on the ground; the Server passes a Jolt character, a test can pass a flat floor. <paramref name="modifiers"/> are the
    /// Modifiers acting on the player, such as the Limb scores, and leave the speed alone when null. <paramref name="modes"/>
    /// are the Movement modes, the starting ones when null, and <paramref name="carriedKilograms"/> is the mass the player
    /// carries, which costs Stamina above <see cref="EncumbranceThresholdKilograms"/>.
    /// </summary>
    public static PlayerMoveState Step(
        in PlayerMoveState state,
        in PlayerInput input,
        float seconds,
        IPlayerCollision collide,
        ModifierSet? modifiers = null,
        MovementModes? modes = null,
        float carriedKilograms = 0f)
    {
        ArgumentNullException.ThrowIfNull(collide);

        var crouched = input.Crouch;
        var wish = WishDirection(input);
        var mode = ModeFor(state, input, modes ?? DefaultModes);
        var moving = wish != Vector3.Zero;
        var target = wish * TargetSpeed(mode, modifiers);
        var velocity = state.Velocity;

        var horizontal = new Vector3(velocity.X, 0, velocity.Z);
        var acceleration = (state.OnGround ? GroundAcceleration : AirAcceleration) * seconds;
        horizontal = MoveTowards(horizontal, target, acceleration);

        var vertical = velocity.Y;
        if (state.OnGround && input.Jump && !crouched)
        {
            vertical = JumpSpeed;
        }
        else
        {
            vertical = MathF.Max(vertical - (Gravity * seconds), -MaxFallSpeed);
        }

        velocity = new Vector3(horizontal.X, vertical, horizontal.Z);

        var height = HeightFor(crouched);
        var moved = collide.Move(state.Position, velocity * seconds, height, seconds);
        var onGround = moved.OnGround;
        if (onGround && velocity.Y < 0)
        {
            velocity = velocity with { Y = 0 };
        }

        var drain = moving ? StaminaDrain(mode, carriedKilograms) : 0f;
        var stamina = drain > 0f
            ? MathF.Max(0f, state.Stamina - (drain * seconds))
            : MathF.Min(FullStamina, state.Stamina + (StaminaRecoveryPerSecond * seconds));
        var exhausted = stamina <= 0f || (state.Exhausted && stamina < ExhaustedUntil);
        var noise = moving ? (float)mode.Noise : 0f;

        return new PlayerMoveState(moved.Position, velocity, input.Yaw, input.Pitch, crouched, onGround, stamina, exhausted, noise);
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDelta)
    {
        var delta = target - current;
        var length = delta.Length();
        return length <= maxDelta || length < 1e-6f ? target : current + (delta * (maxDelta / length));
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static float MoveTowards(float current, float target, float maxDelta)
    {
        var delta = target - current;
        return Math.Abs(delta) <= maxDelta ? target : current + (MathF.Sign(delta) * maxDelta);
    }

    /// <summary>How far the camera leans sideways for the given input, in blocks. Positive leans right.</summary>
    public static float LeanTarget(in PlayerInput input) => (input.LeanRight ? 1f : 0f) - (input.LeanLeft ? 1f : 0f);

    /// <summary>Moves a lean amount toward its target at <see cref="LeanSpeed"/>.</summary>
    public static float StepLean(float lean, in PlayerInput input, float seconds) =>
        MoveTowards(lean, LeanTarget(input) * LeanOffset, LeanSpeed * seconds);
}

/// <summary>What a move resolved to: where the player ended up and whether they are standing on something.</summary>
public readonly record struct PlayerMoveResult(Vector3 Position, bool OnGround);

/// <summary>
/// Resolves one player's moves against the world. A character controller keeps contacts and ground state between steps,
/// so there is one of these per player. The Server and the client both use a Jolt character; tests use a flat floor.
/// </summary>
public interface IPlayerCollision
{
    /// <summary>
    /// Moves a player of the given height by <paramref name="delta"/> from <paramref name="position"/>, stepping up ledges
    /// no higher than <see cref="PlayerMovement.StepHeight"/> and stopping at walls.
    /// </summary>
    PlayerMoveResult Move(Vector3 position, Vector3 delta, float height, float seconds);
}

/// <summary>Creates the collision resolver for one player, at the position and height they start at.</summary>
public interface IPlayerCollisionSource
{
    IPlayerCollision Create(Vector3 position, float height);
}

/// <summary>A flat floor at y = 0, for tests and for a client that has no physics world yet.</summary>
public sealed class FlatFloorCollision : IPlayerCollision, IPlayerCollisionSource
{
    public static FlatFloorCollision Instance { get; } = new();

    public IPlayerCollision Create(Vector3 position, float height) => Instance;

    public PlayerMoveResult Move(Vector3 position, Vector3 delta, float height, float seconds)
    {
        var target = position + delta;
        var onGround = target.Y <= 0f;
        return new PlayerMoveResult(onGround ? target with { Y = 0f } : target, onGround);
    }
}