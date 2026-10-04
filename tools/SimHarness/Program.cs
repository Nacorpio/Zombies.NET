using Zombies.Engine.Core;

// Headless simulation harness. Usage: dotnet run --project tools/SimHarness [ticks]
var ticks = args.Length > 0 && long.TryParse(args[0], out var parsed) ? parsed : 300;

var simulation = new Simulation();
var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 30, ticks: ticks);

Console.WriteLine($"SimHarness: {simulation.CurrentTick} ticks at {Simulation.TickRateHz} Hz, steady-state allocation {allocated} bytes");
return allocated == 0 ? 0 : 1;
