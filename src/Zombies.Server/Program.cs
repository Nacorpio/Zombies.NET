using System.Diagnostics;
using System.Globalization;
using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;
using Zombies.Persistence.Sqlite;

// Headless dedicated Server. The same GameServer runs embedded in the client for solo play.
//   Zombies.Server [--port N] [--seed N] [--mods DIR] [--max-players N] [--key TEXT] [--ticks N] [--save FILE]
//   --port 27015 by default; --ticks stops after N ticks (for smoke runs); Ctrl+C stops cleanly.
//   --save FILE keeps the world in one SQLite file: a new file records the seed, generator, and mods; an existing file
//   supplies the seed and is refused when its generator or mods differ from what is running.
var port = 27015;
ulong seed = 12345;
var seedGiven = false;
string? savePath = null;
string? modsDirectory = null;
var maxPlayers = 4;
var key = "zombies";
long? stopAfter = null;
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

var identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion);
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

            seed = saved.WorldSeed;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: loaded save '{savePath}' (world seed {seed}, schema {save.SchemaVersion})"));
        }
        else
        {
            headers.Write(new SaveHeader(seed, identity.WorldGeneratorVersion, running));
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

var options = new ServerOptions(identity, seed) { MaxPlayers = maxPlayers };
using var transport = LiteNetTransport.Listen(port, key, maxPlayers + 2);
var server = new GameServer(transport, options);
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
