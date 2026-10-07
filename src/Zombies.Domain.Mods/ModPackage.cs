using System.Security.Cryptography;
using System.Text;

namespace Zombies.Domain.Mods;

/// <summary>One definition file of a mod: a path relative to the mod root, and its JSON text.</summary>
public sealed record ModFile(string Path, string Json);

/// <summary>A non-JSON file of a mod, such as an icon: a path relative to the mod root, and its bytes. <see cref="ReadError"/> is set when the file could not be read.</summary>
public sealed record ModAsset(string Path, byte[] Bytes, string? ReadError = null);

/// <summary>A mod as read from disk or memory: where it came from, its manifest text, and its definition files.</summary>
public sealed record ModPackage(string Source, string ManifestJson, IReadOnlyList<ModFile> Files)
{
    /// <summary>Files that are not definitions. The mod loader ignores them; each system that owns a kind of asset reads its own.</summary>
    public IReadOnlyList<ModAsset> Assets { get; init; } = [];

    /// <summary>The C# assemblies a Code mod ships, as <c>assemblies/&lt;file&gt;.dll</c>. Empty for a Data mod.</summary>
    public IReadOnlyList<ModAsset> Assemblies { get; init; } = [];

    /// <summary>
    /// A SHA-256 over the manifest, every definition file, and every assembly, in path order, as lowercase hex. Line endings
    /// of text are normalized first, so a Windows checkout and a Linux checkout of the same mod hash the same; assemblies are
    /// hashed byte for byte, so two builds of a Code mod only match when they are the same file. Join compares these hashes.
    /// </summary>
    public string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "mod.json");
        Append(hash, ManifestJson);
        foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            Append(hash, file.Path);
            Append(hash, file.Json);
        }

        foreach (var assembly in Assemblies.OrderBy(a => a.Path, StringComparer.Ordinal))
        {
            Append(hash, assembly.Path);
            hash.AppendData(assembly.Bytes);
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        hash.AppendData([0]);
    }
}
