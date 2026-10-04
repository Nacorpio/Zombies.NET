using System.Diagnostics;
using System.Globalization;
using Zombies.Client;
using Zombies.Engine.Core.Debugging;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Render.Vulkan;

// Zombies.Client [--no-vsync] [--frames N] [--capture file.png] [--smoke] [--session file.json]
//   --frames N    quit by itself after N frames
//   --capture     save the last frame as a PNG just before quitting
//   --smoke       run a scripted check: draw, resize, minimize, restore, draw again, and report; exit code 0 means it all worked
//   --session F   show a debug session badge (top right) that follows the JSON file F; F2 pins it open, hovering opens it
var options = ClientOptions.Parse(args);

using var window = new SdlWindow("Zombies.NET", 1280, 720);
using var renderer = new VulkanRenderer(window, new RendererOptions(VSync: !options.NoVSync));
var sprites = new SpriteBatch();
var stats = new FrameStats();
var smoke = options.Smoke ? new SmokeScript(window) : null;
var sessionSource = options.SessionPath is { } sessionPath ? new FileSessionSource(sessionPath) : null;
var badge = new SessionBadgeController();

var clock = Stopwatch.GetTimestamp();
var start = clock;
var frame = 0;
var rendered = 0;
var skipped = 0;

while (!window.CloseRequested)
{
    window.PumpEvents();
    if (window.Input.WasPressed(Key.Escape))
    {
        break;
    }

    if (window.Resized)
    {
        renderer.Resize(window.PixelWidth, window.PixelHeight);
    }

    smoke?.Step(frame);

    if (window.IsMinimized)
    {
        skipped++;
        Thread.Sleep(5);
        frame++;
        if (smoke?.Finished == true)
        {
            break;
        }

        continue;
    }

    var now = Stopwatch.GetTimestamp();
    stats.Add(Stopwatch.GetElapsedTime(clock, now).TotalSeconds);
    clock = now;

    sprites.Clear();
    DemoScene.Draw(sprites, renderer.Width, renderer.Height, frame, window.Input);
    DebugOverlay.Draw(sprites, stats, renderer.DeviceName, renderer.Width, renderer.Height);

    var elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    var sessionState = sessionSource?.Poll(elapsedSeconds) ?? SmokeSession.Describe(smoke, frame);
    var badgeLayout = badge.Update(sessionState, elapsedSeconds, DateTimeOffset.UtcNow, window.Input.MouseX, window.Input.MouseY, window.Input.WasPressed(Key.F2), renderer.Width, renderer.Height);
    if (badgeLayout is not null)
    {
        SessionBadge.Draw(sprites, badgeLayout);
    }

    var lastFrame = options.Frames > 0 && frame == options.Frames - 1 || smoke?.WantsCapture(frame) == true;
    if (lastFrame && options.CapturePath is not null)
    {
        renderer.RequestCapture(options.CapturePath);
    }

    if (renderer.Render(sprites, new Rgba(24, 28, 36)))
    {
        rendered++;
    }
    else
    {
        skipped++;
    }

    frame++;
    if (options.Frames > 0 && frame >= options.Frames)
    {
        break;
    }

    if (smoke?.Finished == true)
    {
        break;
    }
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Zombies.Client: {rendered} frames drawn, {skipped} skipped, {stats}, {renderer.Width}x{renderer.Height} on {renderer.DeviceName} ({renderer.PresentationInfo})"));

if (smoke is not null)
{
    return smoke.Report(rendered, renderer.Width, renderer.Height);
}

return 0;
