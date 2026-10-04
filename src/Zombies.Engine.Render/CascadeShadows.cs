using System.Numerics;

namespace Zombies.Engine.Render;

/// <summary>One slice of the view frustum and the matrix that renders the shadows for it.</summary>
public readonly record struct ShadowCascade(Matrix4x4 ViewProjection, float SplitNear, float SplitFar);

/// <summary>
/// Cascaded shadow maps: the view is cut into slices by distance, and each slice gets its own sun-aligned orthographic camera,
/// so shadows are sharp near the viewer and coarser far away. Each cascade is sized to a sphere around its slice and snapped to
/// whole shadow-map texels, which keeps shadow edges from shimmering as the camera moves.
/// </summary>
public static class CascadeShadows
{
    /// <summary>How far toward the sun a cascade reaches to catch shadow casters that lie outside the slice, in blocks. Terrain is 128 high.</summary>
    public const float CasterPadding = 160f;

    /// <summary>
    /// Splits <paramref name="shadowDistance"/> into <paramref name="count"/> slices. A mix of even and logarithmic spacing
    /// puts more resolution near the camera without starving the far slices.
    /// </summary>
    public static float[] Splits(float near, float shadowDistance, int count, float logarithmicWeight = 0.7f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(near, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(shadowDistance, near);

        var splits = new float[count];
        for (var i = 1; i <= count; i++)
        {
            var fraction = (float)i / count;
            var logarithmic = near * MathF.Pow(shadowDistance / near, fraction);
            var uniform = near + ((shadowDistance - near) * fraction);
            splits[i - 1] = (logarithmicWeight * logarithmic) + ((1f - logarithmicWeight) * uniform);
        }

        splits[count - 1] = shadowDistance;
        return splits;
    }

    public static ShadowCascade[] Compute(Camera camera, Vector3 directionToSun, int count, float shadowDistance, int resolution)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 16);

        var toSun = Vector3.Normalize(directionToSun);
        var splits = Splits(camera.Near, shadowDistance, count);
        var cascades = new ShadowCascade[count];
        var previous = camera.Near;
        for (var i = 0; i < count; i++)
        {
            cascades[i] = new ShadowCascade(Fit(camera, toSun, previous, splits[i], resolution), previous, splits[i]);
            previous = splits[i];
        }

        return cascades;
    }

    private static Matrix4x4 Fit(Camera camera, Vector3 toSun, float near, float far, int resolution)
    {
        var corners = SliceCorners(camera, near, far);
        var center = Vector3.Zero;
        foreach (var corner in corners)
        {
            center += corner;
        }

        center /= corners.Length;

        var radius = 0f;
        foreach (var corner in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(corner, center));
        }

        // A fixed-size bound per slice (rounded up) keeps the shadow-map scale constant as the camera turns.
        radius = MathF.Ceiling(radius * 16f) / 16f;

        var up = MathF.Abs(toSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var eye = center + (toSun * (radius + CasterPadding));
        var view = Matrix4x4.CreateLookAt(eye, center, up);

        // Snap the centre to whole texels in light space.
        var texel = 2f * radius / resolution;
        var centerInLight = Vector3.Transform(center, view);
        var snappedX = MathF.Floor(centerInLight.X / texel) * texel;
        var snappedY = MathF.Floor(centerInLight.Y / texel) * texel;

        var depth = 2f * (radius + CasterPadding);
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            snappedX - radius,
            snappedX + radius,
            snappedY - radius,
            snappedY + radius,
            0f,
            depth);

        // CreateOrthographicOffCenter maps -Z (looking away from the eye) to depth 0..1, matching Vulkan.
        return view * projection;
    }

    /// <summary>The eight world-space corners of the part of the view frustum between two distances.</summary>
    public static Vector3[] SliceCorners(Camera camera, float near, float far)
    {
        var forward = camera.Forward;
        var right = camera.Right;
        var up = camera.Up;
        var tan = MathF.Tan(camera.FovY / 2f);

        var corners = new Vector3[8];
        var index = 0;
        foreach (var distance in new[] { near, far })
        {
            var halfHeight = tan * distance;
            var halfWidth = halfHeight * camera.Aspect;
            var middle = camera.Position + (forward * distance);
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                corners[index++] = middle + (right * (halfWidth * sx)) + (up * (halfHeight * sy));
            }
        }

        return corners;
    }
}
