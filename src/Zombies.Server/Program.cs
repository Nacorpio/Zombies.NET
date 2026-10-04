using System.Diagnostics;
using System.Globalization;
using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

// Headless dedicated Server. The same GameServer runs embedded in the client for solo play.
//   Zombies.Server [--port N] [--seed N] [--mods DIR] [--max-players N] [--key TEXT] [--ticks N]
//   --port 27015 by default; --ticks stops after N ticks (for smoke runs); Ctrl+C stops cleanly.
var port = 27015;
ulong seed = 12345;
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
        case "--seed": seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--mods": modsDirectory = Next(); break;
        case "--max-players": maxPlayers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--key": key = Next(); break;
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

var options = new ServerOptions(GameIdentity.From(mods, WorldGenerator.GeneratorVersion), seed) { MaxPlayers = maxPlayers };
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

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Zombies.Server: stopped at tick {simulation.CurrentTick}"));
return 0;
