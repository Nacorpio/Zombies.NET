using System.Numerics;
using System.Runtime.InteropServices;

namespace Zombies.Engine.Render;

/// <summary>
/// One vertex of a player body. Positions are in blocks relative to the body's feet, so a body is drawn by pushing its
/// world position and drawing the same mesh for every player.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BodyVertex(float X, float Y, float Z, float NormalX, float NormalY, float NormalZ, uint Color);

/// <summary>A player body mesh: one vertex and index buffer, drawn with the body pipeline.</summary>
public sealed class BodyMesh
{
    internal BodyMesh(BodyVertex[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }

    public ReadOnlyMemory<BodyVertex> Vertices { get; }

    public ReadOnlyMemory<uint> Indices { get; }

    public int IndexCount => Indices.Length;
}

/// <summary>
/// Builds the simple blocky body a remote player is drawn as: a torso, a head, two arms, and two legs, in the colours of
/// the player's outfit. The mesh is built once and drawn for every player, because a body is only a position and a yaw.
/// </summary>
public static class PlayerBodyMesh
{
    /// <summary>Colours of the parts, in the order the mesh builder uses them.</summary>
    public readonly record struct BodyColors(Rgba Shirt, Rgba Trousers, Rgba Skin, Rgba Hair);

    public static BodyColors DefaultColors { get; } = new(
        new Rgba(74, 106, 148),
        new Rgba(58, 58, 66),
        new Rgba(214, 170, 132),
        new Rgba(58, 44, 34));

    /// <summary>
    /// Builds the body. The parts are sized from <see cref="Net.PlayerMovement"/>'s standing height so the body matches the
    /// collision box the Server moves.
    /// </summary>
    public static BodyMesh Build(BodyColors colors)
    {
        var vertices = new List<BodyVertex>(64);
        var indices = new List<uint>(96);

        // Feet at y = 0, head top at the standing height. Widths are in blocks.
        AddBox(vertices, indices, new Vector3(-0.18f, 0.00f, -0.11f), new Vector3(0.18f, 0.78f, 0.11f), colors.Trousers);
        AddBox(vertices, indices, new Vector3(-0.20f, 0.78f, -0.13f), new Vector3(0.20f, 1.42f, 0.13f), colors.Shirt);
        AddBox(vertices, indices, new Vector3(-0.11f, 1.42f, -0.11f), new Vector3(0.11f, 1.72f, 0.11f), colors.Skin);
        AddBox(vertices, indices, new Vector3(-0.12f, 1.66f, -0.12f), new Vector3(0.12f, 1.80f, 0.12f), colors.Hair);
        AddBox(vertices, indices, new Vector3(-0.30f, 0.82f, -0.09f), new Vector3(-0.20f, 1.40f, 0.09f), colors.Shirt);
        AddBox(vertices, indices, new Vector3(0.20f, 0.82f, -0.09f), new Vector3(0.30f, 1.40f, 0.09f), colors.Shirt);

        return new BodyMesh([.. vertices], [.. indices]);
    }

    /// <summary>Adds one axis-aligned box, with a flat normal per face and no shared vertices.</summary>
    private static void AddBox(List<BodyVertex> vertices, List<uint> indices, Vector3 min, Vector3 max, Rgba color)
    {
        Span<Vector3> corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, max.Y, max.Z),
        ];

        // Faces are numbered +X, -X, +Y, -Y, +Z, -Z, matching the terrain mesher's convention.
        ReadOnlySpan<int> faceCorners =
        [
            1, 5, 6, 2,
            4, 0, 3, 7,
            3, 2, 6, 7,
            4, 5, 1, 0,
            5, 4, 7, 6,
            0, 1, 2, 3,
        ];

        for (var face = 0; face < 6; face++)
        {
            var normal = face switch
            {
                0 => Vector3.UnitX,
                1 => -Vector3.UnitX,
                2 => Vector3.UnitY,
                3 => -Vector3.UnitY,
                4 => Vector3.UnitZ,
                _ => -Vector3.UnitZ,
            };

            var start = (uint)vertices.Count;
            for (var corner = 0; corner < 4; corner++)
            {
                var p = corners[faceCorners[(face * 4) + corner]];
                vertices.Add(new BodyVertex(p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z, color.Packed));
            }

            indices.Add(start);
            indices.Add(start + 2);
            indices.Add(start + 1);
            indices.Add(start);
            indices.Add(start + 3);
            indices.Add(start + 2);
        }
    }
}
