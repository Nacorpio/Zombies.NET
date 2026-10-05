using System.Globalization;
using Zombies.Domain.Mods;

namespace Zombies.Engine.Net;

/// <summary>The first byte of every message.</summary>
public enum MessageType : byte
{
    JoinRequest = 1,
    JoinAccepted,
    JoinRefused,
    Command,
    CommandRejected,
    Snapshot,
    SnapshotAck,
}

public static class NetProtocol
{
    /// <summary>Bump whenever any message layout changes, so old clients are refused instead of misreading.</summary>
    public const ushort Version = 6;
}

/// <summary>Why the Server refused a join.</summary>
public enum JoinRefusal : byte
{
    VersionMismatch = 1,
    ModListMismatch,
    ServerFull,
    WorldOptionMismatch,
    UnknownProfession,
}

/// <summary>What a mod looks like at join: its id, version, and content hash.</summary>
public sealed record ModFingerprint(string Id, string Version, string ContentHash);

/// <summary>The value of a World option that changes the simulation, as it stands on one side of a join.</summary>
public sealed record WorldOptionSetting(string Id, double Value);

/// <summary>
/// Everything that must be identical on a client and the Server for them to play together: the protocol and world generator
/// versions, every mod that runs on the Server with its version and content hash, and the value of every World option
/// that changes the simulation. Client-only mods are left out, because the Server never loads them.
/// </summary>
public sealed record GameIdentity(ushort ProtocolVersion, int WorldGeneratorVersion, IReadOnlyList<ModFingerprint> Mods)
{
    private const int MaxMods = 512;
    private const int MaxWorldOptions = 512;

    /// <summary>The World options that change the simulation, with their effective values, ordered by Content ID.</summary>
    public IReadOnlyList<WorldOptionSetting> WorldOptions { get; init; } = [];

    /// <param name="worldOptions">The World options of this world. Null leaves the identity without any.</param>
    public static GameIdentity From(ModLoadResult mods, int worldGeneratorVersion, WorldOptions? worldOptions = null)
    {
        ArgumentNullException.ThrowIfNull(mods);
        if (!mods.IsSuccess)
        {
            throw new ArgumentException("A mod set that failed to load has no identity.", nameof(mods));
        }

        var fingerprints = mods.Mods
            .Where(m => m.Manifest.Side != ModSide.Client)
            .Select(m => new ModFingerprint(m.Manifest.Id, m.Manifest.Version.ToString(), m.ContentHash))
            .ToList();
        return new GameIdentity(NetProtocol.Version, worldGeneratorVersion, fingerprints)
        {
            WorldOptions = worldOptions?.SimulationValues.Select(v => new WorldOptionSetting(v.Key, v.Value)).ToList() ?? [],
        };
    }

    /// <summary>Returns why <paramref name="client"/> may not join a Server with this identity, or null when it may.</summary>
    public (JoinRefusal Reason, string Detail)? Check(GameIdentity client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.ProtocolVersion != ProtocolVersion || client.WorldGeneratorVersion != WorldGeneratorVersion)
        {
            return (JoinRefusal.VersionMismatch,
                $"The Server runs protocol {ProtocolVersion} with world generator {WorldGeneratorVersion}; this client runs protocol {client.ProtocolVersion} with world generator {client.WorldGeneratorVersion}.");
        }

        var server = Mods.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var mine = client.Mods.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var mod in Mods.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (!mine.TryGetValue(mod.Id, out var other))
            {
                return (JoinRefusal.ModListMismatch, $"The Server runs mod '{mod.Id}' {mod.Version}, which this client does not have.");
            }

            if (other.Version != mod.Version)
            {
                return (JoinRefusal.ModListMismatch, $"Mod '{mod.Id}' is {mod.Version} on the Server but {other.Version} on this client.");
            }

            if (!string.Equals(other.ContentHash, mod.ContentHash, StringComparison.Ordinal))
            {
                return (JoinRefusal.ModListMismatch, $"Mod '{mod.Id}' {mod.Version} has different content on the Server and on this client.");
            }
        }

        foreach (var mod in client.Mods.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (!server.ContainsKey(mod.Id))
            {
                return (JoinRefusal.ModListMismatch, $"This client runs mod '{mod.Id}' {mod.Version}, which the Server does not have.");
            }
        }

        var serverOptions = WorldOptions.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var clientOptions = client.WorldOptions.ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var option in WorldOptions.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (!clientOptions.TryGetValue(option.Id, out var other))
            {
                return (JoinRefusal.WorldOptionMismatch, $"The Server sets world option '{option.Id}' to {Show(option.Value)}, which this client does not have.");
            }

            if (other.Value != option.Value)
            {
                return (JoinRefusal.WorldOptionMismatch, $"World option '{option.Id}' is {Show(option.Value)} on the Server but {Show(other.Value)} on this client.");
            }
        }

        foreach (var option in client.WorldOptions.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (!serverOptions.ContainsKey(option.Id))
            {
                return (JoinRefusal.WorldOptionMismatch, $"This client sets world option '{option.Id}' to {Show(option.Value)}, which the Server does not have.");
            }
        }

        return null;
    }

    private static string Show(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal void Write(NetWriter writer)
    {
        writer.WriteUInt16(ProtocolVersion);
        writer.WriteInt32(WorldGeneratorVersion);
        writer.WriteVarUInt((uint)Mods.Count);
        foreach (var mod in Mods)
        {
            writer.WriteString(mod.Id);
            writer.WriteString(mod.Version);
            writer.WriteString(mod.ContentHash);
        }

        writer.WriteVarUInt((uint)WorldOptions.Count);
        foreach (var option in WorldOptions)
        {
            writer.WriteString(option.Id);
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(option.Value));
        }
    }

    internal static GameIdentity Read(ref NetReader reader)
    {
        var protocol = reader.ReadUInt16();
        var generator = reader.ReadInt32();
        var count = reader.ReadVarUInt();
        if (count > MaxMods)
        {
            throw new MalformedMessageException($"A join lists {count} mods; at most {MaxMods} are allowed.");
        }

        var mods = new List<ModFingerprint>((int)count);
        for (var i = 0; i < count; i++)
        {
            mods.Add(new ModFingerprint(reader.ReadString(), reader.ReadString(), reader.ReadString()));
        }

        var optionCount = reader.ReadVarUInt();
        if (optionCount > MaxWorldOptions)
        {
            throw new MalformedMessageException($"A join lists {optionCount} world options; at most {MaxWorldOptions} are allowed.");
        }

        var options = new List<WorldOptionSetting>((int)optionCount);
        for (var i = 0; i < optionCount; i++)
        {
            options.Add(new WorldOptionSetting(reader.ReadString(), BitConverter.UInt64BitsToDouble(reader.ReadUInt64())));
        }

        return new GameIdentity(protocol, generator, mods) { WorldOptions = options };
    }
}
