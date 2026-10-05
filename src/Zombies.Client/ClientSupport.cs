using System.Globalization;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Debugging;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;

namespace Zombies.Client;

internal sealed class ClientOptions
{
    public bool NoVSync { get; private set; }

    public bool VSyncOverride { get; private set; }

    public int Frames { get; private set; }

    public string? CapturePath { get; private set; }

    public bool Smoke { get; private set; }

    public bool NoWorld { get; private set; }

    /// <summary>Upper limit on frames per second, 0 for none. Useful when vsync is off and the game would otherwise spin at thousands of frames per second.</summary>
    public int MaxFps { get; private set; }

    /// <summary>Diagnostic: stop streaming once the first load has finished, so the geometry stays fixed while the camera moves.</summary>
    public bool FreezeStreaming { get; private set; }

    public int ViewDistance { get; private set; } = 10;

    public bool SizeSpecified { get; private set; }

    public int Width { get; private set; } = 1280;

    public int Height { get; private set; } = 720;

    public float TimeOfDay { get; private set; } = 0.40f;

    public bool FreezeTime { get; private set; }

    public int ShadowCascades { get; private set; } = 3;

    public int ShadowResolution { get; private set; } = 2048;

    public float[]? CameraPose { get; private set; }

    public string? ShotPath { get; private set; }

    public bool Benchmark { get; private set; }

    public double BudgetMilliseconds { get; private set; } = 16.6;

    public string? ModsDirectory { get; private set; }

    public ulong Seed { get; private set; } = 12345;

    public string? SessionPath { get; private set; }

    /// <summary>Diagnostic: draw this many dummy bodies in front of the camera, to check the body render path without a second player.</summary>
    public int DummyBodies { get; private set; }

    public static ClientOptions Parse(string[] args)
    {
        var options = new ClientOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var next = () => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--no-vsync":
                    options.NoVSync = true;
                    break;
                case "--vsync":
                    options.VSyncOverride = true;
                    break;
                case "--frames":
                    options.Frames = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--capture":
                    options.CapturePath = next();
                    break;
                case "--smoke":
                    options.Smoke = true;
                    break;
                case "--no-world":
                    options.NoWorld = true;
                    break;
                case "--max-fps":
                    options.MaxFps = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--freeze-streaming":
                    options.FreezeStreaming = true;
                    break;
                case "--view":
                    options.ViewDistance = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--size":
                    var size = next().Split('x');
                    options.SizeSpecified = true;
                    options.Width = int.Parse(size[0], CultureInfo.InvariantCulture);
                    options.Height = int.Parse(size[1], CultureInfo.InvariantCulture);
                    break;
                case "--time":
                    options.TimeOfDay = float.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--freeze-time":
                    options.FreezeTime = true;
                    break;
                case "--shadows":
                    options.ShadowCascades = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--shadow-res":
                    options.ShadowResolution = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--cam":
                    options.CameraPose = [.. next().Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture))];
                    if (options.CameraPose.Length != 5)
                    {
                        throw new ArgumentException("--cam needs x,y,z,yawDegrees,pitchDegrees.");
                    }

                    break;
                case "--shot":
                    options.ShotPath = next();
                    options.FreezeTime = true;
                    break;
                case "--bench":
                    options.Benchmark = true;
                    options.NoVSync = true;
                    options.FreezeTime = true;
                    break;
                case "--budget-ms":
                    options.BudgetMilliseconds = double.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--mods":
                    options.ModsDirectory = next();
                    break;
                case "--seed":
                    options.Seed = ulong.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--session":
                    options.SessionPath = next();
                    break;
                case "--bodies":
                    options.DummyBodies = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (options.VSyncOverride)
        {
            options.NoVSync = false;
        }

        return options;
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

        // Every loaded icon at 2x with its name, to check the art by eye.
        for (var i = 0; i < Icons.Count; i++)
        {
            var column = i % 8;
            var row = i / 8;
            var cellX = 40 + (column * 96);
            var cellY = 420 + (row * 84);
            sprites.DrawIcon(Icons.Names[i], cellX, cellY, 2, Rgba.White);
            sprites.DrawText(Icons.Names[i], cellX, cellY + (IconSet.Size * 2) + 4, 1, new Rgba(170, 200, 230));
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

/// <summary>Lets a smoke run say what it is and where it is, so a human watching the window knows why it is flickering.</summary>
internal static class SmokeSession
{
    private static readonly string[] StepTitles = ["Input events", "Resize window", "Minimize and restore", "Capture frame"];
    private static readonly int[] StepStarts = [0, 30, 60, 90];

    public static SessionState Describe(SmokeScript? smoke, int frame)
    {
        if (smoke is null)
        {
            return SessionState.None;
        }

        var current = StepStarts.Count(start => frame >= start) - 1;
        var steps = StepTitles
            .Select((title, i) => new SessionStep(title, i < current ? StepState.Done : i == current ? StepState.Running : StepState.Pending))
            .ToList();
        return new SessionState(
            new DebugSession
            {
                Title = "Smoke check",
                Reason = "Proves the window and renderer survive resize, minimize, and restore. The game closes by itself when done.",
                StartedBy = "--smoke",
                Steps = steps,
                Facts = [new SessionFact("Frame", frame.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            },
            null,
            0);
    }
}

/// <summary>Loads the icons of the installed mods into <see cref="Icons"/>. Bad icons are reported and never stop the client.</summary>
internal static class IconSetup
{
    public static void Install(ClientOptions options)
    {
        try
        {
            var packages = DirectoryModSource.Read(options.ModsDirectory ?? DirectoryModSource.Find(AppContext.BaseDirectory));
            var result = IconLoader.Load(packages, ModLoader.Load(packages));
            foreach (var problem in result.Problems)
            {
                Console.Error.WriteLine(problem);
            }

            Icons.Install(result.Icons);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"[Icon] {ex.Message} Placeholder icons are used.");
        }
    }
}
