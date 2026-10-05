using System.Numerics;
using Zombies.Domain.Items;
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

/// <summary>Where a player is and how they are moving. The Server owns this; a client predicts it and reconciles against the Server's copy.</summary>
public readonly record struct PlayerMoveState(Vector3 Position, Vector3 Velocity, float Yaw, float Pitch, bool Crouched, bool OnGround)
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

    public const float WalkSpeed = 4.3f;

    public const float SprintSpeed = 6.5f;

    public const float CrouchSpeed = 1.8f;

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

    /// <summary>Horizontal speed the input asks for, before acceleration, after the <paramref name="modifiers"/> on <see cref="MoveSpeed"/>.</summary>
    public static float TargetSpeed(in PlayerInput input, ModifierSet? modifiers = null)
    {
        var speed = input.Crouch ? CrouchSpeed : input.Sprint ? SprintSpeed : WalkSpeed;
        return modifiers is null ? speed : MathF.Max(0f, (float)modifiers.EffectiveValue(MoveSpeed, speed));
    }

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
    /// Modifiers acting on the player, such as the Limb scores, and leave the speed alone when null.
    /// </summary>
    public static PlayerMoveState Step(in PlayerMoveState state, in PlayerInput input, float seconds, IPlayerCollision collide, ModifierSet? modifiers = null)
    {
        ArgumentNullException.ThrowIfNull(collide);

        var crouched = input.Crouch;
        var wish = WishDirection(input);
        var target = wish * TargetSpeed(input, modifiers);
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

        return new PlayerMoveState(moved.Position, velocity, input.Yaw, input.Pitch, crouched, onGround);
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