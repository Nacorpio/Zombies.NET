using System.Diagnostics;
using System.Globalization;
using Zombies.Domain.Death;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;
using Zombies.Persistence.Sqlite;

// Headless dedicated Server. The same GameServer runs embedded in the client for solo play.
//   Zombies.Server [--port N] [--seed N] [--mods DIR] [--max-players N] [--key TEXT] [--ticks N] [--save FILE] [--option ID=VALUE]... [--list-options] [--scenario ID]
//   --port 27015 by default; --ticks stops after N ticks (for smoke runs); Ctrl+C stops cleanly.
//   --save FILE keeps the world in one SQLite file: a new file records the seed, generator, and mods; an existing file
//   supplies the seed and is refused when its generator or mods differ from what is running.
//   --option sets a world option when the world is created (repeatable); --list-options prints the options the mods declare
//   and stops. A save keeps its options, so an existing --save is refused when --option disagrees with what it recorded.
//   --scenario sets where, when and in what state players begin; it is not saved, so it applies to whoever joins this run.
//   Each player picks their own profession when they join.
var port = 27015;
ulong seed = 12345;
var seedGiven = false;
string? savePath = null;
string? modsDirectory = null;
var maxPlayers = 4;
var key = "zombies";
long? stopAfter = null;
var chosenOptions = new Dictionary<string, double>(StringComparer.Ordinal);
var listOptions = false;
string? scenarioId = null;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--seed": seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); seedGiven = true; break;
        case "--mods": modsDirectory = Next(); break;
        case "--max-players": maxPlayers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--key": key = Next(); break;
        case "--save": savePath = Next(); break;
        case "--option":
            var assignment = Next();
            var equals = assignment.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || !double.TryParse(assignment[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var optionValue))
            {
                Console.Error.WriteLine($"--option needs ID=VALUE, not '{assignment}'.");
                return 2;
            }

            chosenOptions[assignment[..equals]] = optionValue;
            break;
        case "--list-options": listOptions = true; break;
        case "--scenario": scenarioId = Next(); break;
        case "--ticks": stopAfter = long.Parse(Next(), CultureInfo.InvariantCulture); break;
        default:
            Console.Error.WriteLine($"Unknown option '{args[i]}'.");
            return 2;
    }
}

var mods = ModLoader.Load(DirectoryModSource.Read(modsDirectory ?? DirectoryModSource.Find(AppContext.BaseDirectory)));
if (!mods.IsSuccess)
{
    foreach (var error in mods.Errors)
    {
        Console.Error.WriteLine(error);
    }

    return 1;
}

WorldOptionCatalog optionCatalog;
WorldOptions worldOptions;
try
{
    optionCatalog = WorldOptionCatalog.From(mods.Registry);
    worldOptions = new WorldOptions(optionCatalog, chosenOptions);
}
catch (WorldOptionException ex)
{
    Console.Error.WriteLine($"Zombies.Server: {ex.Message}");
    return 2;
}

if (listOptions)
{
    foreach (var option in optionCatalog.All)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{option.Id} ({option.Type.ToString().ToLowerInvariant()}, {option.Min} to {option.Max}, default {option.Default}): {option.Description}"));
    }

    return 0;
}

var identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion, worldOptions);
SaveDatabase? save = null;
if (savePath is not null)
{
    try
    {
        save = SaveDatabase.Open(savePath);
        var headers = new SqliteSaveHeaderRepository(save);
        var running = identity.Mods.Select(m => new SavedMod(m.Id, m.Version, m.ContentHash)).ToList();
        if (headers.Load() is { } saved)
        {
            var problems = saved.CheckAgainst(identity.WorldGeneratorVersion, running);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine($"Zombies.Server: '{savePath}' cannot be loaded with what is running:");
                foreach (var problem in problems)
                {
                    Console.Error.WriteLine($"  {problem}");
                }

                return 1;
            }

            if (seedGiven && seed != saved.WorldSeed)
            {
                Console.Error.WriteLine($"Zombies.Server: '{savePath}' is world seed {saved.WorldSeed}, not {seed}. Leave out --seed to load it.");
                return 2;
            }

            // The save decides its options too, so a world keeps the rules it was created with.
            var savedOptions = saved.Options.Select(o => KeyValuePair.Create(o.Id, o.Value)).ToList();
            try
            {
                var keptOptions = new WorldOptions(optionCatalog, savedOptions);
                if (chosenOptions.Count > 0 && !worldOptions.SimulationValues.SequenceEqual(keptOptions.SimulationValues))
                {
                    Console.Error.WriteLine($"Zombies.Server: '{savePath}' was created with different world options. Leave out --option to load it.");
                    return 2;
                }

                identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion, keptOptions);
            }
            catch (WorldOptionException ex)
            {
                Console.Error.WriteLine($"Zombies.Server: '{savePath}' cannot be loaded: {ex.Message}");
                return 1;
            }

            seed = saved.WorldSeed;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: loaded save '{savePath}' (world seed {seed}, schema {save.SchemaVersion})"));
        }
        else
        {
            headers.Write(new SaveHeader(seed, identity.WorldGeneratorVersion, running) { Options = worldOptions.Chosen.Select(o => new SavedOption(o.Key, o.Value)).ToList() });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: created save '{savePath}' (world seed {seed}, schema {save.SchemaVersion})"));
        }
    }
    catch (SaveException ex)
    {
        Console.Error.WriteLine($"Zombies.Server: {ex.Message}");
        save?.Dispose();
        return 1;
    }
}

var items = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
var scenarios = StartingContentLoader.LoadScenarios(mods.Registry);
Scenario? scenario = null;
if (scenarioId is not null && !scenarios.TryGet(scenarioId, out scenario))
{
    Console.Error.WriteLine($"Zombies.Server: there is no scenario '{scenarioId}'. The mods declare: {string.Join(", ", scenarios.All.Select(s => s.Id))}.");
    return 2;
}

var options = new ServerOptions(identity, seed)
{
    MaxPlayers = maxPlayers,
    Items = items,
    Wearables = StartingContentLoader.LoadWearables(mods.Registry),
    Loot = StartingContentLoader.LoadLoot(mods.Registry),
    Professions = StartingContentLoader.LoadProfessions(mods.Registry),
    Scenario = scenario,
};

// With a save, Corpses and their Containers and the Memorials are kept in it; without one they last as long as the process.
var deathStores = save is null
    ? null
    : new DeathStores(new SqliteContainerRepository(save, items), new SqliteCorpseRepository(save), new SqliteMemorialRepository(save));
using var transport = LiteNetTransport.Listen(port, key, maxPlayers + 2);
var server = new GameServer(transport, options, deathStores);
var simulation = new Simulation(server);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Zombies.Server: listening on UDP {transport.LocalPort}, seed {seed}, {mods.Mods.Count} mods, up to {maxPlayers} players"));

var clock = new FixedStepClock();
var last = Stopwatch.GetTimestamp();
var players = 0;
while (!stop.IsCancellationRequested && (stopAfter is null || simulation.CurrentTick < stopAfter))
{
    var now = Stopwatch.GetTimestamp();
    for (var ticks = clock.Advance(Stopwatch.GetElapsedTime(last, now)); ticks > 0; ticks--)
    {
        simulation.Step();
    }

    last = now;
    if (server.PlayerCount != players)
    {
        players = server.PlayerCount;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: {players} players at tick {simulation.CurrentTick}"));
    }

    Thread.Sleep(1);
}

save?.Dispose();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: stopped at tick {simulation.CurrentTick}"));
return 0;
