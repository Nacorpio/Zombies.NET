using System.Numerics;

namespace Zombies.Engine.Render;

/// <summary>The six planes of a view volume, for skipping anything that cannot be seen. Works for perspective and orthographic matrices with 0 to 1 depth.</summary>
public readonly struct Frustum
{
    private readonly Vector4[] _planes;

    /// <summary>Builds the planes from a combined view-projection matrix in System.Numerics's row-vector convention.</summary>
    public Frustum(Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        var column0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var column1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var column2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var column3 = new Vector4(m.M14, m.M24, m.M34, m.M44);

        _planes =
        [
            Normalize(column3 + column0), // left
            Normalize(column3 - column0), // right
            Normalize(column3 + column1), // bottom
            Normalize(column3 - column1), // top
            Normalize(column2),           // near (depth runs 0 to 1)
            Normalize(column3 - column2), // far
        ];
    }

    /// <summary>False only when the box is entirely outside one plane. A box may be reported as visible when it is merely close to the edge.</summary>
    public bool Intersects(Vector3 min, Vector3 max)
    {
        foreach (var plane in _planes)
        {
            var farthest = new Vector3(
                plane.X >= 0 ? max.X : min.X,
                plane.Y >= 0 ? max.Y : min.Y,
                plane.Z >= 0 ? max.Z : min.Z);
            if ((plane.X * farthest.X) + (plane.Y * farthest.Y) + (plane.Z * farthest.Z) + plane.W < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector4 Normalize(Vector4 plane)
    {
        var length = MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));
        return plane / length;
    }
}
