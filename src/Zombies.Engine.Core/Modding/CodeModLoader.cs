using System.Reflection;
using System.Runtime.Loader;
using Zombies.Domain.Mods;
using Zombies.Modding.Api;

namespace Zombies.Engine.Core.Modding;

/// <summary>A reason the Code mods cannot run. Like a mod load error, any one of them stops the game from starting.</summary>
public sealed record CodeModProblem(string ModId, string Message)
{
    public override string ToString() => $"[Code] {ModId}: {Message}";
}

/// <summary>A Trait a Code mod registered.</summary>
public sealed record ModTrait(string Id, string ModId, TraitSetup Setup);

/// <summary>Reads a message's payload and runs the mod's handler for it. Throws <see cref="MalformedModMessageException"/> for a bad payload.</summary>
public delegate ModMessageResult ModMessagePayloadHandler(ReadOnlySpan<byte> payload, ModMessageSender sender);

/// <summary>A message a Code mod registered, with the number it travels under, which <see cref="ModMessageTable"/> assigned.</summary>
public sealed record ModMessageRegistration(string Id, string ModId, ushort Number, ModMessagePayloadHandler Handle);

/// <summary>A Code mod whose code runs in this process: its manifest, its assemblies, and what it registered.</summary>
public sealed record LoadedCodeMod(ModManifest Manifest, IReadOnlyList<Assembly> Assemblies, IReadOnlyList<ModTrait> Traits, IReadOnlyList<ModMessageRegistration> Messages);

/// <summary>
/// All-or-nothing outcome of running the Code mods of a mod set. On any problem nothing is registered: <see cref="Mods"/> is
/// empty and <see cref="Problems"/> lists every problem found.
/// </summary>
public sealed class CodeModLoadResult
{
    internal CodeModLoadResult(IReadOnlyList<LoadedCodeMod> mods, IReadOnlyList<ModManifest> notRunHere, ModMessageTable messages, IReadOnlyList<CodeModProblem> problems)
    {
        Mods = mods;
        NotRunHere = notRunHere;
        Messages = messages;
        Problems = problems;
    }

    public bool IsSuccess => Problems.Count == 0;

    /// <summary>Code mods whose code runs in this process, in load order.</summary>
    public IReadOnlyList<LoadedCodeMod> Mods { get; }

    /// <summary>Code mods whose declared side keeps their code out of this process, such as a Server mod on a client.</summary>
    public IReadOnlyList<ModManifest> NotRunHere { get; }

    /// <summary>The number every mod message travels under, the same on the Server and every client that may join it.</summary>
    public ModMessageTable Messages { get; }

    public IReadOnlyList<CodeModProblem> Problems { get; }

    public IEnumerable<ModTrait> Traits => Mods.SelectMany(m => m.Traits);

    public IEnumerable<ModMessageRegistration> MessageHandlers => Mods.SelectMany(m => m.Messages);
}

/// <summary>
/// Runs the code of Code mods (ADR 0004). Each mod's assemblies load into their own <see cref="AssemblyLoadContext"/>, sharing
/// only the modding API with the game, and every public <see cref="ICodeMod"/> in them registers what it adds. Code is trusted
/// and is not sandboxed; the player was warned when installing the mod. A mod whose side does not run here is skipped.
/// </summary>
public static class CodeModLoader
{
    private static readonly string ApiAssemblyName = typeof(ICodeMod).Assembly.GetName().Name!;

    /// <param name="packages">The packages <paramref name="mods"/> was loaded from; their assemblies are read from here.</param>
    /// <param name="mods">The mod set, which must have loaded.</param>
    /// <param name="role">What this process runs, which decides which mods' code runs.</param>
    public static CodeModLoadResult Load(IReadOnlyList<ModPackage> packages, ModLoadResult mods, ProcessRole role)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(mods);
        if (!mods.IsSuccess)
        {
            throw new ArgumentException("Code mods run only from a mod set that loaded.", nameof(mods));
        }

        var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (ModManifest.TryParse(package.ManifestJson, out var manifest, out _))
            {
                byId.TryAdd(manifest.Id, package);
            }
        }

        var problems = new List<CodeModProblem>();
        var notRunHere = new List<ModManifest>();
        var registrars = new List<(ModManifest Manifest, IReadOnlyList<Assembly> Assemblies, Registrar Registrar)>();
        var traitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loaded in mods.Mods.Where(m => m.Manifest.IsCodeMod))
        {
            var manifest = loaded.Manifest;
            if (!manifest.Side.RunsCodeIn(role))
            {
                notRunHere.Add(manifest);
                continue;
            }

            if (!byId.TryGetValue(manifest.Id, out var package))
            {
                problems.Add(new CodeModProblem(manifest.Id, "The mod's package was not given, so its assemblies cannot be read."));
                continue;
            }

            var registrar = new Registrar(manifest.Id, traitIds);
            var assemblies = Run(manifest, package, registrar, problems);
            registrars.Add((manifest, assemblies, registrar));
        }

        var table = ModMessageTable.Assign(mods, registrars.ToDictionary(r => r.Manifest.Id, r => (IReadOnlyList<string>)[.. r.Registrar.Messages.Keys], StringComparer.Ordinal), problems);
        if (problems.Count > 0)
        {
            return new CodeModLoadResult([], notRunHere, ModMessageTable.Empty, problems);
        }

        var result = registrars
            .Select(r => new LoadedCodeMod(
                r.Manifest,
                r.Assemblies,
                r.Registrar.Traits,
                [.. r.Registrar.Messages
                    .OrderBy(m => m.Key, StringComparer.Ordinal)
                    .Select(m => new ModMessageRegistration(m.Key, r.Manifest.Id, table.NumberOf(m.Key), m.Value))]))
            .ToList();
        return new CodeModLoadResult(result, notRunHere, table, []);
    }

    private static List<Assembly> Run(ModManifest manifest, ModPackage package, Registrar registrar, List<CodeModProblem> problems)
    {
        var shipped = package.Assemblies
            .Where(a => a.Path.StartsWith(ModLoader.AssemblyFolder, StringComparison.Ordinal) && a.ReadError is null)
            .ToDictionary(a => a.Path[ModLoader.AssemblyFolder.Length..], a => a.Bytes, StringComparer.OrdinalIgnoreCase);
        var context = new ModLoadContext(manifest.Id, shipped);
        var assemblies = new List<Assembly>();
        foreach (var name in manifest.Assemblies)
        {
            Assembly assembly;
            Type[] types;
            try
            {
                assembly = context.LoadShipped(name);
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TypeLoadException or KeyNotFoundException)
            {
                problems.Add(new CodeModProblem(manifest.Id, $"The assembly '{name}' could not be loaded: {ex.Message}"));
                continue;
            }

            assemblies.Add(assembly);
            var entries = types
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ICodeMod).IsAssignableFrom(t))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
            if (entries.Count == 0)
            {
                problems.Add(new CodeModProblem(manifest.Id, $"The assembly '{name}' has no public class implementing {nameof(ICodeMod)}."));
                continue;
            }

            foreach (var type in entries)
            {
                try
                {
                    var mod = (ICodeMod)Activator.CreateInstance(type)!;
                    mod.Register(registrar);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    problems.Add(new CodeModProblem(manifest.Id, $"{type.FullName} failed to start: {ex.InnerException.Message}"));
                }
                catch (MissingMethodException)
                {
                    problems.Add(new CodeModProblem(manifest.Id, $"{type.FullName} needs a public constructor without parameters."));
                }
#pragma warning disable CA1031 // A trusted mod may throw anything from its own code; every failure is reported, never crashes the loader.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    problems.Add(new CodeModProblem(manifest.Id, $"{type.FullName} failed to register: {ex.Message}"));
                }
            }
        }

        return assemblies;
    }

    /// <summary>One mod's assemblies, loaded from the bytes it shipped. The modding API and the runtime come from the game.</summary>
    private sealed class ModLoadContext(string modId, Dictionary<string, byte[]> shipped) : AssemblyLoadContext($"mod:{modId}")
    {
        public Assembly LoadShipped(string fileName)
        {
            using var stream = new MemoryStream(shipped[fileName], writable: false);
            return LoadFromStream(stream);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || string.Equals(assemblyName.Name, ApiAssemblyName, StringComparison.Ordinal)
                || !shipped.ContainsKey(assemblyName.Name + ".dll"))
            {
                return null;
            }

            return LoadShipped(assemblyName.Name + ".dll");
        }
    }

    /// <summary>Records what one mod registers, refusing names outside its namespace and names already taken.</summary>
    private sealed class Registrar(string modId, HashSet<string> traitIds) : IModRegistry
    {
        public string ModId { get; } = modId;

        public List<ModTrait> Traits { get; } = [];

        public Dictionary<string, ModMessagePayloadHandler> Messages { get; } = new(StringComparer.Ordinal);

        public void RegisterTrait(string traitId, TraitSetup setup)
        {
            ArgumentNullException.ThrowIfNull(setup);
            Require(traitId, "trait", nameof(traitId));
            if (!traitIds.Add(traitId))
            {
                throw new ArgumentException($"The Trait '{traitId}' is already registered.", nameof(traitId));
            }

            Traits.Add(new ModTrait(traitId, ModId, setup));
        }

        public void RegisterMessage<TMessage>(ModMessageHandler<TMessage> handler)
            where TMessage : IModMessage<TMessage>
        {
            ArgumentNullException.ThrowIfNull(handler);
            var id = TMessage.Id;
            Require(id, "message", nameof(TMessage));
            if (Messages.ContainsKey(id))
            {
                throw new ArgumentException($"The message '{id}' is already registered.", nameof(TMessage));
            }

            Messages[id] = (payload, sender) =>
            {
                var reader = new ModMessageReader(payload);
                var message = TMessage.Read(ref reader);
                reader.EnsureEnd();
                return handler(in message, sender);
            };
        }

        private void Require(string? id, string kind, string parameter)
        {
            if (!ContentId.TryParse(id, out var parsed) || !string.Equals(parsed.Kind, kind, StringComparison.Ordinal) || parsed.Path == kind)
            {
                throw new ArgumentException($"'{id}' must be a Content ID of kind '{kind}', such as '{ModId}:{kind}/name'.", parameter);
            }

            if (!string.Equals(parsed.Namespace, ModId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{id}' is in namespace '{parsed.Namespace}', but mod '{ModId}' may only register in its own.", parameter);
            }
        }
    }
}
