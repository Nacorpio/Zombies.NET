using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Voxel;
using Zombies.Modding.Api;

namespace Zombies.Engine.Tests;

/// <summary>
/// The sample Code mod in <c>mods/sample_code</c>, loaded from its built assembly the way the game loads it, and run on a
/// Server and clients over the in-memory transport.
/// </summary>
public sealed class CodeModTests
{
    private const string ShoutId = "sample_code:message/shout";
    private const string ScreamerTrait = "sample_code:trait/screamer";

    private static IReadOnlyList<ModPackage> RepositoryPackages() => DirectoryModSource.Read(RepositoryMods.Root);

    /// <summary>The repository mods with the sample Code mod's manifest changed to declare another side.</summary>
    private static List<ModPackage> WithSampleSide(string side, params ModPackage[] extra) =>
    [
        .. RepositoryPackages().Select(p => p.Source == "sample_code"
            ? p with { ManifestJson = p.ManifestJson.Replace("\"side\": \"both\"", $"\"side\": \"{side}\"", StringComparison.Ordinal) }
            : p),
        .. extra,
    ];

    private static ModPackage DataMod(string id, string side) =>
        new(id, $$"""{ "id": "{{id}}", "version": "1.0.0", "side": "{{side}}" }""", []);

    private static (ModLoadResult Mods, CodeModLoadResult Code) Load(IEnumerable<ModPackage> all, ProcessRole role, IReadOnlyList<string>? preferredOrder = null)
    {
        var packages = ModSides.ForRole(all, role);
        var mods = ModLoader.Load(packages, preferredOrder);
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        return (mods, CodeModLoader.Load(packages, mods, role));
    }

    private static ushort ShoutNumber(CodeModLoadResult code)
    {
        Assert.True(code.IsSuccess, string.Join(Environment.NewLine, code.Problems));
        Assert.True(code.Messages.TryGetNumber(ShoutId, out var number), "The shout message has no number.");
        return number;
    }

    private static byte[] ShoutPayload(float loudness)
    {
        var writer = new ModMessageWriter();
        writer.WriteSingle(loudness);
        return writer.Written.ToArray();
    }

    [Fact]
    public void TheSampleCodeMod_RegistersATraitAndAMessage_ThroughThePublicApi()
    {
        var code = RepositoryMods.For(ProcessRole.Server).Code;

        var mod = Assert.Single(code.Mods);
        Assert.Equal("sample_code", mod.Manifest.Id);
        Assert.Equal([ScreamerTrait], mod.Traits.Select(t => t.Id));
        Assert.Equal([ShoutId], mod.Messages.Select(m => m.Id));
        Assert.Empty(code.NotRunHere);
    }

    [Fact]
    public void AScreamerZombie_SpawnsWithTheComponentTheModsTraitAdds()
    {
        var loaded = RepositoryMods.For(ProcessRole.Server);
        var catalog = ZombieContentLoader.Load(loaded.Mods.Registry);
        var system = new ZombieSystem(new ServerWorld(), catalog, RepositoryMods.Traits(), RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));

        Assert.True(system.TrySpawn(new Zombies.Domain.Zombies.ZombieSpec(3, "sample_code:zombie/screamer", 1), Vector3.Zero, 0f, out var id, out var problem), problem);
        Assert.True(system.TryGetEntity(id, out var entity));

        // The component type belongs to the mod's own assembly, so it is found through the assembly the game loaded.
        var screamer = Assert.Single(loaded.Code.Mods).Assemblies.Single().GetType("SampleCodeMod.Screamer", throwOnError: true)!;
        var arguments = new object?[] { entity, null };
        var found = (bool)typeof(EcsWorld).GetMethod(nameof(EcsWorld.TryGet))!.MakeGenericMethod(screamer).Invoke(system.Ecs, arguments)!;
        Assert.True(found);
        Assert.Equal(0.8, (double)screamer.GetProperty("Volume")!.GetValue(arguments[1])!);
        Assert.True(system.Ecs.Has<Shambler>(entity));
    }

    [Fact]
    public void AZombieTypeThatListsTheModsTrait_CannotRunWithoutTheCodeMod()
    {
        var catalog = ZombieContentLoader.Load(RepositoryMods.For(ProcessRole.Server).Mods.Registry);
        var baseOnly = new TraitRegistry();
        BaseTraits.Register(baseOnly);

        var ex = Assert.Throws<ArgumentException>(() => new ZombieSystem(new ServerWorld(), catalog, baseOnly, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid")));
        Assert.Contains(ScreamerTrait, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClientsShout_ReachesTheModsHandlerOnTheServer_AndABadOneIsRefusedWithTheModsReason()
    {
        var server = RepositoryMods.For(ProcessRole.Server);
        var client = RepositoryMods.For(ProcessRole.Client);
        var network = new InMemoryNetwork();
        var gameServer = new GameServer(network.CreateServer(), new ServerOptions(GameIdentity.From(server.Mods, WorldGenerator.GeneratorVersion), WorldSeed: 9));
        CodeModMessages.Register(gameServer.Commands, server.Code);
        var alice = new GameClient(network.Connect(), GameIdentity.From(client.Mods, WorldGenerator.GeneratorVersion), "alice");
        long tick = 0;
        void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                gameServer.Tick(tick++);
                alice.Poll();
            }
        }

        Run(3);
        Assert.Equal(ClientState.Joined, alice.State);
        var number = ShoutNumber(client.Code);

        alice.Send(number, ShoutPayload(0.5f));
        Run(2);
        Assert.Equal(0, alice.RejectionCount);

        var loud = alice.Send(number, ShoutPayload(3f));
        Run(2);
        Assert.Equal(1, alice.RejectionCount);
        Assert.Equal(loud, alice.LastRejection!.Sequence);
        Assert.Equal(CommandRejection.Invalid, alice.LastRejection.Reason);
        Assert.Contains("alice shouted at loudness 3", alice.LastRejection.Detail, StringComparison.Ordinal);

        var cut = alice.Send(number, [0x00, 0x00]);
        Run(2);
        Assert.Equal(cut, alice.LastRejection!.Sequence);
        Assert.Equal(CommandRejection.Malformed, alice.LastRejection.Reason);
    }

    [Fact]
    public void ShoutGetsTheNumberOfItsModsPlaceInTheSortedModList()
    {
        // The Server runs base, sample_code, and sample_data. Sorted by id, sample_code is second, so it owns the second block.
        Assert.Equal(ModMessageTable.FirstNumber + ModMessageTable.MaxPerMod, ShoutNumber(RepositoryMods.For(ProcessRole.Server).Code));
        Assert.Equal(ModMessageTable.FirstNumber + ModMessageTable.MaxPerMod, ShoutNumber(RepositoryMods.For(ProcessRole.Client).Code));
        Assert.Equal(ModMessageTable.FirstNumber + ModMessageTable.MaxPerMod, ShoutNumber(RepositoryMods.For(ProcessRole.Solo).Code));
    }

    [Fact]
    public void MessageNumbers_DoNotDependOnInstallOrLoadOrder()
    {
        var usual = ShoutNumber(Load(RepositoryPackages(), ProcessRole.Server).Code);

        var reversed = Load(RepositoryPackages().Reverse(), ProcessRole.Server, preferredOrder: ["sample_data", "sample_code"]);

        Assert.NotEqual(["base", "sample_code", "sample_data"], reversed.Mods.Mods.Select(m => m.Manifest.Id));
        Assert.Equal(usual, ShoutNumber(reversed.Code));
    }

    [Fact]
    public void AModSortedBeforeIt_ShiftsTheNumber_TheSameWayOnTheServerAndOnEveryClient()
    {
        // A Server-only mod is part of what a client must match at join, so it counts on both sides, though its code would run only on the Server.
        var packages = WithSampleSide("both", DataMod("aaa_server_rules", "server"));

        var onServer = ShoutNumber(Load(packages, ProcessRole.Server).Code);
        var onClient = ShoutNumber(Load(packages, ProcessRole.Client).Code);

        Assert.Equal(ModMessageTable.FirstNumber + (2 * ModMessageTable.MaxPerMod), onServer);
        Assert.Equal(onServer, onClient);
    }

    [Fact]
    public void AClientOnlyModSortedBeforeIt_DoesNotShiftTheNumber_SinceTheServerNeverLoadsIt()
    {
        var packages = WithSampleSide("both", DataMod("aaa_client_hud", "client"));

        var onServer = Load(packages, ProcessRole.Server);
        var onClient = Load(packages, ProcessRole.Client);

        Assert.DoesNotContain(onServer.Mods.Mods, m => m.Manifest.Id == "aaa_client_hud");
        Assert.Contains(onClient.Mods.Mods, m => m.Manifest.Id == "aaa_client_hud");
        Assert.Equal(ModMessageTable.FirstNumber + ModMessageTable.MaxPerMod, ShoutNumber(onServer.Code));
        Assert.Equal(ShoutNumber(onServer.Code), ShoutNumber(onClient.Code));
    }

    [Fact]
    public void ADedicatedServer_DoesNotLoadAClientOnlyMod_AndAClientWithOneStillJoins()
    {
        var packages = WithSampleSide("both", DataMod("client_hud", "client"));
        var server = Load(packages, ProcessRole.Server);
        var client = Load(packages, ProcessRole.Client);
        var network = new InMemoryNetwork();
        var gameServer = new GameServer(network.CreateServer(), new ServerOptions(GameIdentity.From(server.Mods, WorldGenerator.GeneratorVersion), WorldSeed: 1));
        var bob = new GameClient(network.Connect(), GameIdentity.From(client.Mods, WorldGenerator.GeneratorVersion), "bob");

        for (var tick = 0; tick < 3; tick++)
        {
            gameServer.Tick(tick);
            bob.Poll();
        }

        Assert.Equal(["base", "sample_code", "sample_data"], server.Mods.Mods.Select(m => m.Manifest.Id));
        Assert.Equal(ClientState.Joined, bob.State);
    }

    [Fact]
    public void AClientOnlyCodeMod_NeverRunsOnADedicatedServer()
    {
        var packages = WithSampleSide("client");

        var server = Load(packages, ProcessRole.Server);

        Assert.DoesNotContain(server.Mods.Mods, m => m.Manifest.Id == "sample_code");
        Assert.True(server.Code.IsSuccess);
        Assert.Empty(server.Code.Mods);
        Assert.False(server.Code.Messages.TryGetNumber(ShoutId, out _));
    }

    [Fact]
    public void AClientOnlyCodeMod_ThatRegistersAMessage_IsRefusedWhereItRuns()
    {
        var client = Load(WithSampleSide("client"), ProcessRole.Client);

        Assert.False(client.Code.IsSuccess);
        var problem = Assert.Single(client.Code.Problems);
        Assert.Equal("sample_code", problem.ModId);
        Assert.Contains("client-only mod cannot register a message", problem.Message, StringComparison.Ordinal);
        Assert.Empty(client.Code.Mods);
    }

    [Fact]
    public void AServerOnlyCodeMod_RunsOnTheServerAndInSolo_ButOnAClientOnlyItsDataLoads()
    {
        var packages = WithSampleSide("server");

        var server = Load(packages, ProcessRole.Server);
        var solo = Load(packages, ProcessRole.Solo);
        var client = Load(packages, ProcessRole.Client);

        Assert.Equal(["sample_code"], server.Code.Mods.Select(m => m.Manifest.Id));
        Assert.Equal(["sample_code"], solo.Code.Mods.Select(m => m.Manifest.Id));
        Assert.Empty(client.Code.Mods);
        Assert.Equal(["sample_code"], client.Code.NotRunHere.Select(m => m.Id));
        Assert.True(client.Mods.Registry.TryGet(ContentIdOf("sample_code:zombie/screamer"), out _));
        Assert.Equal(GameIdentity.From(server.Mods, 1), GameIdentity.From(client.Mods, 1), new IdentityComparer());
    }

    [Fact]
    public void ACodeModWhoseAssemblyIsMissing_FailsToLoad_InsteadOfRunningAsData()
    {
        var packages = RepositoryPackages().Select(p => p.Source == "sample_code" ? p with { Assemblies = [] } : p).ToList();

        var mods = ModLoader.Load(packages);

        Assert.False(mods.IsSuccess);
        var error = Assert.Single(mods.Errors);
        Assert.Equal(ModLoadErrorKind.MissingAssembly, error.Kind);
        Assert.Contains("SampleCodeMod.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeModsContentHash_CoversItsAssembly()
    {
        var sample = RepositoryPackages().Single(p => p.Source == "sample_code");
        var assembly = Assert.Single(sample.Assemblies);
        var changed = (byte[])assembly.Bytes.Clone();
        changed[^1] ^= 0xFF;

        Assert.NotEqual(sample.ComputeContentHash(), (sample with { Assemblies = [assembly with { Bytes = changed }] }).ComputeContentHash());
    }

    private static ContentId ContentIdOf(string text) => ContentId.TryParse(text, out var id) ? id : throw new ArgumentException(text);

    private sealed class IdentityComparer : IEqualityComparer<GameIdentity>
    {
        public bool Equals(GameIdentity? x, GameIdentity? y) => x is not null && y is not null && x.Check(y) is null && y.Check(x) is null;

        public int GetHashCode(GameIdentity obj) => 0;
    }
}
