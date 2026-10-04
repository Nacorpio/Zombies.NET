using System.Globalization;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;

namespace Zombies.Client;

internal sealed record ClientOptions(bool NoVSync, int Frames, string? CapturePath, bool Smoke)
{
    public static ClientOptions Parse(string[] args)
    {
        var noVSync = false;
        var frames = 0;
        string? capture = null;
        var smoke = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-vsync":
                    noVSync = true;
                    break;
                case "--frames" when i + 1 < args.Length:
                    frames = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--capture" when i + 1 < args.Length:
                    capture = args[++i];
                    break;
                case "--smoke":
                    smoke = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new ClientOptions(noVSync, frames, capture, smoke);
    }
}

/// <summary>Some shapes and text that make a screenshot easy to judge: alpha blending, scaling, and every character of the font.</summary>
internal static class DemoScene
{
    public static void Draw(SpriteBatch sprites, int width, int height, int frame, InputState input)
    {
        // A grid of colored swatches fading in alpha, to show blending against the background.
        for (var i = 0; i < 8; i++)
        {
            var hue = i * 32;
            sprites.FillRect(40 + (i * 90), 200, 80, 80, new Rgba((byte)(255 - hue), (byte)hue, 160, 255));
            sprites.FillRect(40 + (i * 90), 290, 80, 40, new Rgba(255, 255, 255, (byte)(32 + (i * 28))));
        }

        sprites.DrawText("ZOMBIES.NET - VULKAN SPRITE PASS", 40, 100, 3, Rgba.White);
        sprites.DrawText("0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ", 40, 140, 2, new Rgba(160, 220, 255));
        sprites.DrawText(".:-/%(),+=_|", 40, 164, 2, new Rgba(160, 220, 255));
        sprites.DrawText("SCALE 1 TEXT FOR SMALL LABELS", 40, 350, 1, new Rgba(200, 200, 200));
        sprites.DrawText("SCALE 4", 40, 370, 4, new Rgba(255, 200, 80));

        // Every built-in icon at 3x with its name, to check the art by eye.
        for (var i = 0; i < Icons.Count; i++)
        {
            var column = i % 8;
            var row = i / 8;
            var cellX = 40 + (column * 96);
            var cellY = 420 + (row * 84);
            sprites.DrawIcon(Icons.Names[i], cellX, cellY, 3, Rgba.White);
            sprites.DrawText(Icons.Names[i], cellX, cellY + 52, 1, new Rgba(170, 200, 230));
        }

        // A box that moves, so a live run shows the frame loop is running.
        var x = 40 + (frame * 3 % Math.Max(1, width - 120));
        sprites.FillRect(x, Math.Max(0, height - 80), 60, 40, new Rgba(120, 240, 140));

        // Show that input reaches the game: the box turns red while W is held.
        if (input.IsDown(Key.W))
        {
            sprites.FillRect(40, 440, 60, 40, new Rgba(240, 90, 90));
            sprites.DrawText("W HELD", 110, 452, 2, Rgba.White);
        }
    }
}

/// <summary>A scripted run that proves the window and renderer survive resize, minimize, and restore, and can capture a frame.</summary>
internal sealed class SmokeScript(SdlWindow window)
{
    private const int ResizeAt = 30;
    private const int MinimizeAt = 60;
    private const int ExtraFramesAfterRestore = 30;

    private int _minimizedFrames;
    private int _restoredAt = -1;
    private bool _sawMinimized;
    private bool _sawResize;
    private bool _keyDownArrived;
    private bool _keyUpArrived;

    public bool Finished { get; private set; }

    public bool WantsCapture(int frame) => _restoredAt >= 0 && frame == _restoredAt + ExtraFramesAfterRestore - 1;

    public void Step(int frame)
    {
        // Push a real SDL key event and check it arrives in the input state one pump later.
        if (frame == 5)
        {
            SdlWindow.PushKeyEvent(Key.W, down: true);
        }

        if (frame == 6)
        {
            _keyDownArrived = window.Input.IsDown(Key.W) && window.Input.WasPressed(Key.W);
        }

        if (frame == 8)
        {
            SdlWindow.PushKeyEvent(Key.W, down: false);
        }

        if (frame == 9)
        {
            _keyUpArrived = !window.Input.IsDown(Key.W) && window.Input.WasReleased(Key.W);
        }

        if (frame == ResizeAt)
        {
            window.SetSize(800, 600);
        }

        if (window.Resized && frame > ResizeAt)
        {
            _sawResize = true;
        }

        if (frame == MinimizeAt)
        {
            window.Minimize();
        }

        if (window.IsMinimized)
        {
            _sawMinimized = true;
            _minimizedFrames++;
            if (_minimizedFrames == 20)
            {
                window.Restore();
            }
        }
        else if (_sawMinimized && _restoredAt < 0)
        {
            _restoredAt = frame;
        }

        if (_restoredAt >= 0 && frame >= _restoredAt + ExtraFramesAfterRestore)
        {
            Finished = true;
        }

        // Safety net: never run forever if the window manager ignores a request.
        if (frame > 2000)
        {
            Finished = true;
        }
    }

    public int Report(int rendered, int width, int height)
    {
        var problems = new List<string>();
        if (!_sawResize)
        {
            problems.Add("never saw the resize event");
        }

        if (!_sawMinimized)
        {
            problems.Add("never saw the minimize event");
        }

        if (_restoredAt < 0)
        {
            problems.Add("never saw the restore event");
        }

        if (!_keyDownArrived)
        {
            problems.Add("a pushed key-down event never reached the input state");
        }

        if (!_keyUpArrived)
        {
            problems.Add("a pushed key-up event never reached the input state");
        }

        if (rendered < 90)
        {
            problems.Add($"drew only {rendered} frames");
        }

        if (width <= 0 || height <= 0)
        {
            problems.Add($"ended with size {width}x{height}");
        }

        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"SMOKE FAIL: {problem}");
        }

        Console.WriteLine(problems.Count == 0 ? "SMOKE OK" : "SMOKE FAILED");
        return problems.Count == 0 ? 0 : 1;
    }
}
