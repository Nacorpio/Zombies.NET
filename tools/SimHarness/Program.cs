using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;

// Headless harness.
//   dotnet run --project tools/SimHarness [ticks]       tick a simulation and check it does not allocate
//   dotnet run --project tools/SimHarness mods <dir>    load every mod in <dir> and list what loaded
//   dotnet run --project tools/SimHarness schemas <dir> write JSON Schema files for definitions into <dir>
if (args.Length > 0 && args[0] == "mods")
{
    return LoadMods(args.Length > 1 ? args[1] : "mods");
}

if (args.Length > 0 && args[0] == "schemas")
{
    return WriteSchemas(args.Length > 1 ? args[1] : "schemas");
}

var ticks = args.Length > 0 && long.TryParse(args[0], out var parsed) ? parsed : 300;

var simulation = new Simulation();
var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 30, ticks: ticks);

Console.WriteLine($"SimHarness: {simulation.CurrentTick} ticks at {Simulation.TickRateHz} Hz, steady-state allocation {allocated} bytes");
return allocated == 0 ? 0 : 1;

static int LoadMods(string directory)
{
    var result = ModLoader.Load(DirectoryModSource.Read(directory));
    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine(error);
    }

    if (!result.IsSuccess)
    {
        return 1;
    }

    Console.WriteLine($"SimHarness: loaded {result.Mods.Count} mods, {result.Registry.Count} definitions");
    foreach (var mod in result.Mods)
    {
        Console.WriteLine($"  {mod.Order}: {mod.Manifest.Id} {mod.Manifest.Version}");
    }

    return 0;
}

static int WriteSchemas(string directory)
{
    Directory.CreateDirectory(directory);
    foreach (var (kind, schema) in DefinitionSchemas.Generate())
    {
        var path = Path.Combine(directory, $"{kind}.schema.json");
        File.WriteAllText(path, schema);
        Console.WriteLine($"SimHarness: wrote {path}");
    }

    return 0;
}
