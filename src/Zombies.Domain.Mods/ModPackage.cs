using System.Security.Cryptography;
using System.Text;

namespace Zombies.Domain.Mods;

/// <summary>One definition file of a mod: a path relative to the mod root, and its JSON text.</summary>
public sealed record ModFile(string Path, string Json);

/// <summary>A mod as read from disk or memory: where it came from, its manifest text, and its definition files.</summary>
public sealed record ModPackage(string Source, string ManifestJson, IReadOnlyList<ModFile> Files)
{
    /// <summary>
    /// A SHA-256 over the manifest and every file, in path order, as lowercase hex. Line endings are normalized first,
    /// so a Windows checkout and a Linux checkout of the same mod hash the same. Join compares these hashes.
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

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        hash.AppendData([0]);
    }
}
