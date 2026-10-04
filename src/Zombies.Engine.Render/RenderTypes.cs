using System.Runtime.InteropServices;

namespace Zombies.Engine.Render;

/// <summary>An 8-bit-per-channel color, not premultiplied.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba White => new(255, 255, 255);

    public static Rgba Black => new(0, 0, 0);

    /// <summary>Bytes in memory order R, G, B, A, which is what an R8G8B8A8_UNORM vertex attribute reads.</summary>
    public uint Packed => (uint)(R | (G << 8) | (B << 16) | (A << 24));

    public Rgba WithAlpha(byte alpha) => this with { A = alpha };
}

/// <summary>One corner of a sprite quad. Positions are in pixels from the top-left of the window.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpriteVertex(float X, float Y, float U, float V, uint Color);

public readonly record struct RendererOptions(bool VSync = true);

/// <summary>What every graphics backend offers. The game talks to this; Vulkan and, later, Direct3D 11 implement it.</summary>
public interface IRenderer : IDisposable
{
    string DeviceName { get; }

    /// <summary>Size of the drawable area in pixels. Zero while the window is minimized.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>Tells the renderer the drawable size changed. The swapchain is rebuilt before the next frame.</summary>
    void Resize(int width, int height);

    /// <summary>
    /// Clears the screen and draws the batch. Returns false when no frame was drawn, for example while minimized
    /// or while the swapchain is being rebuilt; callers just try again next frame.
    /// </summary>
    bool Render(SpriteBatch sprites, Rgba clear);

    /// <summary>Saves the next drawn frame as a PNG. Used by smoke tests and bug reports.</summary>
    void RequestCapture(string pngPath);
}
