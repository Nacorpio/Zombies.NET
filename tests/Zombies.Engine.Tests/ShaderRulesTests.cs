using System.Text.RegularExpressions;

namespace Zombies.Engine.Tests;

/// <summary>Rules for the HLSL sources that exist because breaking them crashed the GPU once.</summary>
public sealed partial class ShaderRulesTests
{
    private static string ShaderFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        return Path.Combine(root, "src", "Zombies.Engine.Render.Vulkan", "Shaders");
    }

    [Fact]
    public void Shaders_DoNotDeclareConstantArrays()
    {
        // Indexing a constant array with a runtime value makes the compiler copy it into per-thread local memory.
        // On NVIDIA driver 582 that produced an intermittent GPU device loss (an invalid write reported by VK_EXT_device_fault)
        // whenever frames overlapped. Compute the value with arithmetic or a switch instead. See FaceNormal in terrain.frag.hlsl.
        var offenders = Directory.EnumerateFiles(ShaderFolder(), "*.hlsl")
            .Where(file => ConstantArray().IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"These shaders declare a constant array, which can crash the GPU when indexed at runtime: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ShaderFolder_ContainsTheShadersTheRendererLoads()
    {
        var names = Directory.EnumerateFiles(ShaderFolder(), "*.hlsl").Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(["body.frag.hlsl", "body.vert.hlsl", "shadow.vert.hlsl", "sprite.frag.hlsl", "sprite.vert.hlsl", "terrain.frag.hlsl", "terrain.vert.hlsl"], names);
    }

    [GeneratedRegex(@"^\s*(static\s+const|const\s+static)\s+[A-Za-z0-9_]+\s+[A-Za-z0-9_]+\s*\[", RegexOptions.Multiline)]
    private static partial Regex ConstantArray();
}
