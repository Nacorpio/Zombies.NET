namespace Zombies.Engine.Animation;

/// <summary>
/// The size of the voxels character meshes are built from. A terrain block is one meter, and a character voxel is an eighth
/// of one, so a body part can have detail finer than a block. Rig data is written in voxels and converted here.
/// </summary>
public static class VoxelScale
{
    /// <summary>How many character voxels make one meter.</summary>
    public const int PerMeter = 8;

    /// <summary>The edge of one character voxel, in meters. An exact power of two, so conversions lose nothing.</summary>
    public const float VoxelMeters = 1f / PerMeter;

    public static float ToMeters(float voxels) => voxels * VoxelMeters;

    public static int ToVoxels(float meters) => (int)MathF.Round(meters * PerMeter);
}
