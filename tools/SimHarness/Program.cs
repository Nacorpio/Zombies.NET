using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Render;

// Headless harness.
//   dotnet run --project tools/SimHarness [ticks]       tick a simulation and check it does not allocate
//   dotnet run --project tools/SimHarness mods <dir>    load every mod in <dir> and list what loaded
//   dotnet run --project tools/SimHarness schemas <dir> write JSON Schema files for definitions into <dir>
//   dotnet run --project tools/SimHarness worldgen [seed] [radius] [--budget-ms N]   generate, light, and mesh a patch of world and time it
//   dotnet run --project tools/SimHarness net [ticks] [mods]   run a Server with two fake clients and check replication and allocation
//   dotnet run --project tools/SimHarness physics [seed] [mods]   drop a player onto generated terrain with Jolt and step up a ledge
//   dotnet run --project tools/SimHarness ui [mods]   load every string table and layout and check they fit the screen
//   dotnet run --project tools/SimHarness ai [seed] [--ticks N] [--budget-ms N] [--zombies N]   time Server ticks with 4 players and 200 zombies thinking
//   dotnet run --project tools/SimHarness codemods [mods]   run the Code mods as a Server, a client, and solo, and check sides and message numbers
//   dotnet run --project tools/SimHarness install <mod> <mods-root> [--trust] [--lang CODE]   install a mod, showing a Code mod's trust warning
if (args.Length > 0 && args[0] == "codemods")
{
    return CodeModReport.Run(args[1..]);
}

if (args.Length > 0 && args[0] == "install")
{
    return CodeModReport.Install(args[1..]);
}
if (args.Length > 0 && args[0] == "mods")
{
    return LoadMods(args.Length > 1 ? args[1] : "mods");
}

if (args.Length > 0 && args[0] == "schemas")
{
    return WriteSchemas(args.Length > 1 ? args[1] : "schemas");
}

if (args.Length > 0 && args[0] == "net")
{
    return NetReport.Run(args[1..]);
}

if (args.Length > 0 && args[0] == "physics")
{
    return PhysicsReport.Run(args[1..]);
}

if (args.Length > 0 && args[0] == "worldgen")
{
    return WorldGenReport.Run(args[1..]);
}

if (args.Length > 0 && args[0] == "ai")
{
    return AiReport.Run(args[1..]);
}

if (args.Length > 0 && args[0] == "ui")
{
    return UiReport.Run(args[1..]);
}

var ticks = args.Length > 0 && long.TryParse(args[0], out var parsed) ? parsed : 300;

var simulation = new Simulation();
var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 30, ticks: ticks);

Console.WriteLine($"SimHarness: {simulation.CurrentTick} ticks at {Simulation.TickRateHz} Hz, steady-state allocation {allocated} bytes");
return allocated == 0 ? 0 : 1;

static int LoadMods(string directory)
{
    var packages = DirectoryModSource.Read(directory);
    var result = ModLoader.Load(packages);
    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine(error);
    }

    if (!result.IsSuccess)
    {
        return 1;
    }

    var icons = IconLoader.Load(packages, result);
    foreach (var problem in icons.Problems)
    {
        Console.Error.WriteLine(problem);
    }

    Console.WriteLine($"SimHarness: loaded {result.Mods.Count} mods, {result.Registry.Count} definitions, {icons.Icons.Count} icons");
    foreach (var mod in result.Mods)
    {
        Console.WriteLine($"  {mod.Order}: {mod.Manifest.Id} {mod.Manifest.Version}");
    }

    return icons.Problems.Count == 0 ? 0 : 1;
}

static int WriteSchemas(string directory)
{
    Directory.CreateDirectory(directory);
    foreach (var (kind, schema) in DefinitionSchemas.Generate(BaseDefinitionKinds.All))
    {
        var path = Path.Combine(directory, $"{kind}.schema.json");
        File.WriteAllText(path, schema);
        Console.WriteLine($"SimHarness: wrote {path}");
    }

    return 0;
}
