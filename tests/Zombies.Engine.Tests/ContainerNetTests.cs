using System.Numerics;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>
/// Opening and looting a world Container through the Server: the client names the block, the Server fills it from the room's
/// Area type, and the client's view follows the Server's answer.
/// </summary>
public sealed class ContainerNetTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private sealed class Rig
    {
        public Rig()
        {
            var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigRoot(), "mods")));
            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
            var items = new ItemCatalog(loaded.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
            var areas = new AreaTypeCatalog(loaded.Registry.OfKind("area_type").Select(d => AreaTypeJson.Parse(d.Json)));
            var loot = new AreaLootService(areas, new LootService(new LootTableCatalog(loaded.Registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)))));

            Network = new InMemoryNetwork();
            Server = new GameServer(Network.CreateServer(), new ServerOptions(Identity, WorldSeed: 12345) { Items = items });
            Server.ContainerOpener = new WorldContainerOpener(loot, items, new InMemoryContainerRepository());

            // A kitchen cabinet and a bathroom cabinet, so the loot differs by the room's Area type. They sit at eye height so a
            // level look reaches them, which is what the Interact key does.
            Kitchen = new WorldContainer(10, 65, 10, "cabinet", "kitchen", "base:area_type/kitchen", 1, 111);
            Bathroom = new WorldContainer(12, 65, 10, "cabinet", "bathroom", "base:area_type/bathroom", 1, 222);
            Server.WorldContainers = new Fixed([Kitchen, Bathroom]);

            Client = new GameClient(Network.Connect(), Identity, "alice");
            for (var i = 0; i < 5; i++)
            {
                Server.Tick(i);
                Client.Poll();
            }

            Assert.Equal(ClientState.Joined, Client.State);
        }

        public InMemoryNetwork Network { get; }

        public GameServer Server { get; }

        public GameClient Client { get; }

        public WorldContainer Kitchen { get; }

        public WorldContainer Bathroom { get; }

        public PlayerSession Player
        {
            get
            {
                Assert.True(Server.TryGetPlayer(new ConnectionId(1), out var session));
                return session;
            }
        }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(100 + i);
                Client.Poll();
            }
        }

        /// <summary>Points the player at a Container and opens it, as the Interact key does.</summary>
        public void Open(WorldContainer container)
        {
            // Feet one block below the Container's block, so the eye at 1.6 m is inside it, 2.5 blocks south and facing north (-Z).
            var feet = new Vector3(container.X + 0.5f, container.Y - 1f, container.Z + 2.5f);
            Server.Teleport(Player, feet, 0f);
            Client.Send(new OpenContainer(container.X, container.Y, container.Z));
            Run(2);
        }

        private sealed class Fixed(IReadOnlyList<WorldContainer> containers) : IWorldContainerSource
        {
            public IReadOnlyList<WorldContainer> Near(Vector3 position, float radius) => containers;
        }

        private static string RigRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    [Fact]
    public void OpeningAContainer_FillsItFromItsRoom_AndTheClientSeesIt()
    {
        var rig = new Rig();
        rig.Open(rig.Kitchen);

        var view = rig.Client.Containers;
        Assert.Equal(2, view.Containers.Count);
        var opened = view.Containers[1];
        Assert.Equal(WorldContainerOpener.IdOf(rig.Kitchen), opened.Id);
        Assert.NotEmpty(opened.Stacks);
    }

    [Fact]
    public void AKitchenAndABathroom_GiveDifferentLoot()
    {
        var kitchen = new Rig();
        kitchen.Open(kitchen.Kitchen);
        var kitchenItems = kitchen.Client.Containers.Containers[1].Stacks.Select(s => s.Item.Value).Order().ToList();

        var bathroom = new Rig();
        bathroom.Open(bathroom.Bathroom);
        var bathroomItems = bathroom.Client.Containers.Containers[1].Stacks.Select(s => s.Item.Value).Order().ToList();

        Assert.NotEqual(kitchenItems, bathroomItems);
    }

    [Fact]
    public void TwoPlayersOpeningTheSameContainer_SeeTheSameContents()
    {
        var rig = new Rig();
        var bob = new GameClient(rig.Network.Connect(), Identity, "bob");
        for (var i = 0; i < 5; i++)
        {
            rig.Server.Tick(200 + i);
            rig.Client.Poll();
            bob.Poll();
        }

        rig.Open(rig.Kitchen);
        Assert.True(rig.Server.TryGetPlayer(new ConnectionId(2), out var bobSession));
        rig.Server.Teleport(bobSession, new Vector3(rig.Kitchen.X + 0.5f, rig.Kitchen.Y - 1f, rig.Kitchen.Z + 2.5f), 0f);
        bob.Send(new OpenContainer(rig.Kitchen.X, rig.Kitchen.Y, rig.Kitchen.Z));
        rig.Run(2);
        bob.Poll();

        var alice = rig.Client.Containers.Containers[1].Stacks.Select(s => (s.Item.Value, s.Count)).Order().ToList();
        var bobs = bob.Containers.Containers[1].Stacks.Select(s => (s.Item.Value, s.Count)).Order().ToList();
        Assert.Equal(alice, bobs);
    }

    [Fact]
    public void MovingAStack_FromTheContainerToTheBackpack_IsAppliedByTheServer()
    {
        var rig = new Rig();
        rig.Open(rig.Kitchen);

        var opened = rig.Client.Containers.Containers[1];
        var stack = opened.Stacks[0];
        var backpack = rig.Client.Containers.Containers[0].Id;
        rig.Client.Send(new MoveStackCommand(opened.Id, stack.Id, backpack, stack.Count));
        rig.Run(2);

        var after = rig.Client.Containers.Containers[0];
        Assert.Contains(after.Stacks, s => s.Item == stack.Item);
    }

    [Fact]
    public void OpeningABlockWithNoContainer_IsRefused()
    {
        var rig = new Rig();
        rig.Client.Send(new OpenContainer(999, 64, 999));
        rig.Run(2);

        Assert.NotNull(rig.Client.LastRejection);
        Assert.Equal(CommandRejection.Invalid, rig.Client.LastRejection!.Reason);
    }
}
