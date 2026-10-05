using UnitsNet;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.World;
using Zombies.Engine.Voxel;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

/// <summary>A save file in the temp folder that is deleted with it.</summary>
internal sealed class TempSave : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zombies-save-").FullName;

    public TempSave() => Path = System.IO.Path.Combine(_directory, "world.sqlite");

    public string Path { get; }

    public SaveDatabase Open() => SaveDatabase.Open(Path);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal static class Fixtures
{
    public static readonly ItemId Beans = new("base:item/canned_beans");
    public static readonly ItemId Water = new("base:item/water_bottle");
    public static readonly ItemId Shirt = new("base:item/shirt");
    public static readonly ItemId Jacket = new("base:item/jacket");

    public static ItemCatalog Items { get; } = new(
    [
        new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
        new ItemDefinition(Water, Mass.FromKilograms(1.0), Volume.FromLiters(1.0), maxStack: 2),
    ]);

    public static WearableCatalog Wearables { get; } = new(
    [
        new WearableDefinition(Shirt, ClothingLayer.Base, [BodyPart.Torso, BodyPart.LeftArm, BodyPart.RightArm], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.05)),
        new WearableDefinition(
            Jacket,
            ClothingLayer.Outer,
            [BodyPart.Torso, BodyPart.LeftArm, BodyPart.RightArm],
            ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1),
            new Dictionary<DamageType, double> { [DamageType.Cut] = 0.4 }),
    ]);

    public static WorldGenerator Generator { get; } = new(12345, new BiomeCatalog([new Biome("base:biome/temperate_forest", ["forest", "grassland"], (0, 100), (0, 100), 52, 14, 50)]));
}
