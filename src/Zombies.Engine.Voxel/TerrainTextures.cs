using Zombies.Domain.World;

namespace Zombies.Engine.Voxel;

/// <summary>
/// Placeholder block textures made in code: a 16 by 16 tile for every block face, with per-pixel variation so surfaces do not look flat.
/// They exist so the world can be seen before any real art does; hand-painted textures replace them later. Tile numbers match
/// <see cref="Blocks.Tile"/>, and every pixel is opaque.
/// </summary>
public static class TerrainTextures
{
    public const int TileSize = 16;

    /// <summary>One tile per face kind (top, side, bottom) of each block id up to and including the lamp.</summary>
    public const int TileCount = (Blocks.Lamp + 1) * 3;

    private static readonly (byte R, byte G, byte B) Magenta = (255, 0, 255);

    /// <summary>Tiles as RGBA bytes, tile after tile, each <see cref="TileSize"/> squared.</summary>
    public static byte[] Generate()
    {
        var data = new byte[TileCount * TileSize * TileSize * 4];
        for (ushort block = 0; block <= Blocks.Lamp; block++)
        {
            for (var kind = 0; kind < 3; kind++)
            {
                var tile = (block * 3) + kind;
                for (var y = 0; y < TileSize; y++)
                {
                    for (var x = 0; x < TileSize; x++)
                    {
                        var (r, g, b) = Pixel(block, kind, x, y);
                        var offset = (((tile * TileSize) + y) * TileSize + x) * 4;
                        data[offset] = r;
                        data[offset + 1] = g;
                        data[offset + 2] = b;
                        data[offset + 3] = 255;
                    }
                }
            }
        }

        return data;
    }

    private static (byte R, byte G, byte B) Pixel(ushort block, int kind, int x, int y)
    {
        var grain = (int)(WorldHash.Mix((ulong)((block * 3) + kind), x, y, 7) % 32) - 16;

        return block switch
        {
            Blocks.Air => Magenta,
            Blocks.Bedrock => Shade((50, 50, 55), grain),
            Blocks.Stone => Shade((125, 125, 130), grain + (((x + (y / 3)) % 7 == 0) ? -22 : 0)),
            Blocks.Dirt => Shade((120, 84, 56), grain),
            Blocks.Grass => kind switch
            {
                0 => Shade((84, 150, 60), grain),
                2 => Shade((120, 84, 56), grain),
                _ => y < 4 + (int)(WorldHash.Mix(11, x, 0, 3) % 2) ? Shade((84, 150, 60), grain) : Shade((120, 84, 56), grain),
            },
            Blocks.Log => kind == 1 ? Shade((98, 72, 44), grain + (x % 4 == 0 ? -20 : 0)) : Shade((150, 118, 76), grain + (Ring(x, y) ? -26 : 0)),
            Blocks.Leaves => Shade((52, 120, 48), grain * 2),
            Blocks.Lamp => Shade((255, 214, 120), grain / 2),
            _ => Magenta,
        };
    }

    private static bool Ring(int x, int y)
    {
        var dx = (2 * x) - TileSize + 1;
        var dy = (2 * y) - TileSize + 1;
        var distance = (dx * dx) + (dy * dy);
        return distance % 70 < 14;
    }

    private static (byte R, byte G, byte B) Shade((int R, int G, int B) color, int offset) => (
        (byte)Math.Clamp(color.R + offset, 0, 255),
        (byte)Math.Clamp(color.G + offset, 0, 255),
        (byte)Math.Clamp(color.B + offset, 0, 255));
}
