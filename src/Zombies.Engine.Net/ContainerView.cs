using System.Numerics;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.World;

namespace Zombies.Engine.Net;

/// <summary>
/// Where the Server finds the Containers in the world, so a player can open one. The Server owns the world's Containers; a
/// client only names the block it is looking at, and the Server decides what is there.
/// </summary>
public interface IWorldContainerSource
{
    /// <summary>The Containers within <paramref name="radius"/> blocks of a point, for targeting and for opening.</summary>
    IReadOnlyList<WorldContainer> Near(Vector3 position, float radius);
}

/// <summary>A world with no Containers, so nothing can be opened.</summary>
public sealed class NoWorldContainers : IWorldContainerSource
{
    public static NoWorldContainers Instance { get; } = new();

    public IReadOnlyList<WorldContainer> Near(Vector3 position, float radius) => [];
}

/// <summary>One Container as the Server sends it to a client: its id, its title, and its Stacks.</summary>
public sealed record ReplicatedContainer(ContainerId Id, string Title, IReadOnlyList<ItemStack> Stacks, double MassFraction, double VolumeFraction);

/// <summary>
/// The Containers a client can see: the one it carries, and any world Container it has opened. The Server sends this whole
/// view whenever it changes, so the client's inventory screen always shows the Server's truth.
/// </summary>
public sealed record ContainerView(IReadOnlyList<ReplicatedContainer> Containers)
{
    public static ContainerView Empty { get; } = new([]);
}

internal static class ContainerViewCodec
{
    private const int MaxContainers = 64;
    private const int MaxStacks = 4096;

    public static void Write(NetWriter writer, ContainerView view)
    {
        writer.WriteVarUInt((uint)view.Containers.Count);
        foreach (var container in view.Containers)
        {
            writer.WriteUInt64((ulong)container.Id.Value);
            writer.WriteString(container.Title);
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(container.MassFraction));
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(container.VolumeFraction));
            writer.WriteVarUInt((uint)container.Stacks.Count);
            foreach (var stack in container.Stacks)
            {
                writer.WriteInt32(stack.Id.Value);
                writer.WriteString(stack.Item.Value);
                writer.WriteInt32(stack.Count);
                WriteState(writer, stack.State);
            }
        }
    }

    public static ContainerView Read(ref NetReader reader)
    {
        var containers = new List<ReplicatedContainer>();
        for (var i = Count(ref reader, MaxContainers); i > 0; i--)
        {
            var id = new ContainerId((long)reader.ReadUInt64());
            var title = reader.ReadString();
            var mass = BitConverter.UInt64BitsToDouble(reader.ReadUInt64());
            var volume = BitConverter.UInt64BitsToDouble(reader.ReadUInt64());
            var stacks = new List<ItemStack>();
            for (var s = Count(ref reader, MaxStacks); s > 0; s--)
            {
                var stackId = new StackId(reader.ReadInt32());
                var itemText = reader.ReadString();
                if (!ItemId.TryParse(itemText, out var item))
                {
                    throw new MalformedMessageException($"'{itemText}' is not a Content ID.");
                }

                var count = reader.ReadInt32();
                stacks.Add(new ItemStack(stackId, item, count, ReadState(ref reader, item)));
            }

            containers.Add(new ReplicatedContainer(id, title, stacks, mass, volume));
        }

        return new ContainerView(containers);
    }

    private static void WriteState(NetWriter writer, ItemState? state)
    {
        var values = state?.Values ?? new Dictionary<string, int>();
        writer.WriteVarUInt((uint)values.Count);
        foreach (var (name, value) in values)
        {
            writer.WriteString(name);
            writer.WriteInt32(value);
        }

        var attached = state?.Attached ?? new Dictionary<ItemId, int>();
        writer.WriteVarUInt((uint)attached.Count);
        foreach (var (item, count) in attached)
        {
            writer.WriteString(item.Value);
            writer.WriteInt32(count);
        }
    }

    private static ItemState? ReadState(ref NetReader reader, ItemId item)
    {
        var values = new List<KeyValuePair<string, int>>();
        for (var i = Count(ref reader, MaxStacks); i > 0; i--)
        {
            values.Add(new(reader.ReadString(), reader.ReadInt32()));
        }

        var attached = new List<KeyValuePair<ItemId, int>>();
        for (var i = Count(ref reader, MaxStacks); i > 0; i--)
        {
            var text = reader.ReadString();
            attached.Add(new(ItemId.TryParse(text, out var parsed) ? parsed : throw new MalformedMessageException($"'{text}' is not a Content ID."), reader.ReadInt32()));
        }

        if (values.Count == 0 && attached.Count == 0)
        {
            return null;
        }

        try
        {
            return ItemState.Create(values, attached);
        }
        catch (ArgumentException ex)
        {
            throw new MalformedMessageException(ex.Message, ex);
        }
    }

    private static uint Count(ref NetReader reader, int max)
    {
        var count = reader.ReadVarUInt();
        return count <= max ? count : throw new MalformedMessageException($"A container message lists {count} entries; at most {max} are allowed.");
    }
}
