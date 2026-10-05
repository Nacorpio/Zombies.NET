namespace Zombies.Engine.Voxel;

/// <summary>The block ids the engine knows. Block definitions will become data later; this is the minimal set for terrain and trees.</summary>
public static class Blocks
{
    public const ushort Air = 0;
    public const ushort Bedrock = 1;
    public const ushort Stone = 2;
    public const ushort Dirt = 3;
    public const ushort Grass = 4;
    public const ushort Log = 5;
    public const ushort Leaves = 6;
    public const ushort Lamp = 7;
    public const ushort Planks = 8;

    /// <summary>The block a Container is drawn as. Which loot it holds comes from the Area type of its room, not from the block.</summary>
    public const ushort Crate = 9;

    /// <summary>Highest block light a source can emit.</summary>
    public const int MaxLight = 15;

    private static readonly bool[] OpaqueTable = [false, true, true, true, true, true, true, true, true, true];
    private static readonly byte[] EmissionTable = [0, 0, 0, 0, 0, 0, 0, 14, 0, 0];

    /// <summary>Block names as Structures spell them, indexed by block id.</summary>
    private static readonly string[] Names = ["air", "bedrock", "stone", "dirt", "grass", "log", "leaves", "lamp", "planks", "crate"];

    /// <summary>Finds a block by the name a Structure's palette uses, such as <c>planks</c>.</summary>
    public static bool TryFromName(string name, out ushort id)
    {
        var index = Array.IndexOf(Names, name);
        id = index < 0 ? Air : (ushort)index;
        return index >= 0;
    }

    public static string NameOf(ushort id) => id < Names.Length ? Names[id] : string.Empty;

    public static bool IsOpaque(ushort id) => id < OpaqueTable.Length && OpaqueTable[id];

    /// <summary>Block light the block gives off, from 0 to <see cref="MaxLight"/>.</summary>
    public static int Emission(ushort id) => id < EmissionTable.Length ? EmissionTable[id] : 0;

    /// <summary>Texture tile for a block face. Faces are numbered +X, -X, +Y, -Y, +Z, -Z; +Y uses the top tile and -Y the bottom tile.</summary>
    public static int Tile(ushort id, int face) => (id * 3) + (face == 2 ? 0 : face == 3 ? 2 : 1);
}
