namespace Zombies.Engine.Render;

/// <summary>
/// Where a player is looking, turned by the mouse. It is kept apart from the <see cref="Camera"/> because a first-person camera
/// is put back at the predicted player every frame, and a turn that lived only on the camera would be thrown away by any frame
/// that did not also send an input command.
/// </summary>
public sealed class PlayerAim(float yaw = 0f, float pitch = 0f)
{
    /// <summary>Turn around the vertical axis, in radians. Positive turns to the right.</summary>
    public float Yaw { get; private set; } = yaw;

    /// <summary>Look up (positive) or down (negative), in radians, clamped just short of straight up or down.</summary>
    public float Pitch { get; private set; } = Math.Clamp(pitch, -Camera.MaxPitch, Camera.MaxPitch);

    /// <summary>Turns by a mouse movement in pixels. <paramref name="radiansPerPixel"/> is the sensitivity.</summary>
    public void Turn(float deltaX, float deltaY, float radiansPerPixel = 0.0025f)
    {
        Yaw += deltaX * radiansPerPixel;
        Pitch = Math.Clamp(Pitch - (deltaY * radiansPerPixel), -Camera.MaxPitch, Camera.MaxPitch);
    }
}
