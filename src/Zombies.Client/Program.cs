using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Zombies.Client;
using Zombies.Engine.Core.Debugging;
using Zombies.Engine.Net;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Render.Vulkan;

// Zombies.Client [options]  (--vsync forces vsync on even in --bench)
//   --view N          view distance in chunks (default 10)      --size WxH        window size (default 1280x720)
//   --time F          time of day, 0 midnight to 1 (default .4) --freeze-time     stop the sky clock
//   --shadows N       shadow cascades 0 to 3 (default 3)        --shadow-res N    shadow map size (default 2048)
//   --cam x,y,z,yaw,pitch   start pose (angles in degrees)      --seed N          world seed
//   --no-vsync        do not wait for the display               --no-world        show the 2D sprite demo only
//   --max-fps N       frame limit, 0 for none                   --freeze-streaming  stop loading chunks after the first load (diagnostic)
// Set ZOMBIES_VK_DIAG=1 to have a lost GPU report the last command it reached and the driver's fault address.
//   --frames N        quit after N frames                       --capture f.png   save the last frame
//   --shot f.png      load the world, wait until it settles, save a picture, quit
//   --bench           fly a fixed path at 1080p and report frame times; exit code 1 if over --budget-ms (default 16.6)
//   --session F       show a debug session badge (top right) that follows the JSON file F; F4 pins it open, hovering opens it
//   --smoke           scripted resize / minimize / restore / input check; exit code 0 means it all worked
// Interactive: WASD fly, Space and Ctrl up and down, Shift fast, Tab mouse look, F1 overlay, F2 pause time, F3 day speed, Left/Right arrows change time.
var options = ClientOptions.Parse(args);
var width = options.Benchmark && !options.SizeSpecified ? 1920 : options.Width;
var height = options.Benchmark && !options.SizeSpecified ? 1080 : options.Height;

IconSetup.Install(options);

using var window = new SdlWindow("Zombies.NET", width, height);
using var renderer = new VulkanRenderer(window, new RendererOptions(!options.NoVSync, options.ShadowCascades, options.ShadowResolution));
using var world = options.NoWorld ? null : new WorldSession(options, renderer);

var camera = new Camera { Position = new Vector3(8, 84, 8), Yaw = 0.5f, Pitch = -0.38f, Far = (options.ViewDistance * 16f) + 64f };
if (options.CameraPose is { } pose)
{
    camera.Position = new Vector3(pose[0], pose[1], pose[2]);
    camera.Yaw = pose[3] * MathF.PI / 180f;
    camera.Pitch = pose[4] * MathF.PI / 180f;
}

// The body mesh every remote player is drawn with. Built once; a body is a position and a yaw.
if (world is not null)
{
    renderer.SetBodyMesh(PlayerBodyMesh.Build(PlayerBodyMesh.DefaultColors));
}

var clock = new DayClock(options.TimeOfDay, secondsPerDay: 24f * 60f) { Paused = options.FreezeTime };
var sprites = new SpriteBatch();
var stats = new FrameStats();
var smoke = options.Smoke ? new SmokeScript(window) : null;
var benchmark = options.Benchmark ? new BenchmarkRun() : null;

var dayLengths = new[] { 24f * 60f, 120f, 20f };
var dayLength = 0;
var showOverlay = true;
var mouseCaptured = false;

var clockStart = Stopwatch.GetTimestamp();
var runStart = clockStart;
var sessionSource = options.SessionPath is { } sessionPath ? new FileSessionSource(sessionPath) : null;
var badge = new SessionBadgeController();
var frame = 0;
var rendered = 0;
var skipped = 0;
var settledFrames = 0;
var shotRequestedAt = -1;
var streamingFrozen = false;
(bool Loaded, float Seconds, double Update) benchmarkObservation = default;

while (!window.CloseRequested)
{
    window.PumpEvents();
    var input = window.Input;
    if (input.WasPressed(Key.Escape))
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
    var seconds = (float)Stopwatch.GetElapsedTime(clockStart, now).TotalSeconds;
    stats.Add(seconds);
    clockStart = now;

    if (input.WasPressed(Key.Tab))
    {
        mouseCaptured = !mouseCaptured;
        window.SetRelativeMouse(mouseCaptured);
    }

    if (input.WasPressed(Key.F1))
    {
        showOverlay = !showOverlay;
    }

    if (input.WasPressed(Key.F2))
    {
        clock.Paused = !clock.Paused;
    }

    if (input.WasPressed(Key.F3))
    {
        dayLength = (dayLength + 1) % dayLengths.Length;
        clock.SecondsPerDay = dayLengths[dayLength];
        clock.Paused = false;
    }

    if (input.IsDown(Key.Left) || input.IsDown(Key.Right))
    {
        clock.Set(clock.Fraction + ((input.IsDown(Key.Right) ? 1 : -1) * 0.1f * seconds));
    }

    clock.Advance(seconds);
    camera.Aspect = renderer.Width > 0 && renderer.Height > 0 ? (float)renderer.Width / renderer.Height : camera.Aspect;

    var sun = SunModel.At(clock.Fraction);
    var flying = benchmark?.Flying ?? false;
    if (flying)
    {
        benchmark!.Advance(camera, seconds);
    }
    else if (benchmark is null && world is null)
    {
        FlyController.Update(camera, input, seconds, mouseCaptured);
    }

    sprites.Clear();
    WorldScene? scene = null;
    var updateMilliseconds = 0.0;
    if (world is not null)
    {
        // The player's input drives the Server and the local prediction; the camera follows the predicted player.
        if (benchmark is null)
        {
            if (mouseCaptured)
            {
                camera.Look(input.MouseDeltaX, input.MouseDeltaY);
            }

            var playerInput = PlayerController.Read(input, camera.Yaw, camera.Pitch, mouseCaptured);
            world.Solo.Client.SendInput(playerInput);
        }

        world.Solo.Advance(TimeSpan.FromSeconds(seconds));
        world.StepPhysics(TimeSpan.FromSeconds(seconds));

        var updateStart = Stopwatch.GetTimestamp();
        if (!streamingFrozen)
        {
            world.Manager.Update(camera.Position, options.ShotPath is not null || benchmark is not null ? 64 : 6);
            streamingFrozen = options.FreezeStreaming && world.Manager.PendingCount == 0 && world.Manager.LoadedCount > 0;
        }

        world.SyncPhysicsTerrain();
        updateMilliseconds = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;

        if (benchmark is null)
        {
            PlayerController.ApplyToCamera(camera, world.Solo.Client.Local.State, world.Solo.Client.Local.Lean);
        }

        scene = new WorldScene(
            camera,
            sun,
            world.Manager.Chunks,
            world.ViewDistance,
            new ShadowSettings(options.ShadowCascades, options.ShadowResolution, Math.Min(150f, world.ViewDistance * 16f)))
        {
            Bodies = RemoteBodies(world, options, camera),
        };
    }
    else
    {
        DemoScene.Draw(sprites, renderer.Width, renderer.Height, frame, input);
    }

    if (showOverlay)
    {
        DebugOverlay.Draw(sprites, stats, renderer.DeviceName, renderer.Width, renderer.Height);
        if (world is not null)
        {
            var time = TimeSpan.FromDays(clock.Fraction);
            var pausedNote = clock.Paused ? " PAUSED" : string.Empty;
            sprites.FillRect(8, 96, 400, 64, new Rgba(0, 0, 0, 150));
            sprites.DrawText(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CHUNKS {world.Manager.LoadedCount}  LOADING {world.Manager.PendingCount}\nSECTIONS DRAWN {renderer.VisibleSectionCount}\nVIEW {world.ViewDistance}  TIME {time.Hours:00}:{time.Minutes:00}{pausedNote}"),
                16,
                104,
                2,
                new Rgba(235, 235, 235));
            sprites.FillRect(8, renderer.Height - 32, 1130, 24, new Rgba(0, 0, 0, 150));
            sprites.DrawText("WASD FLY  SPACE/CTRL UP/DOWN  SHIFT FAST  TAB MOUSE  F1 HUD  F2 TIME  F3 SPEED  ARROWS TIME", 16, renderer.Height - 24, 2, new Rgba(200, 200, 200, 200));
        }
    }

    var elapsedSeconds = Stopwatch.GetElapsedTime(runStart).TotalSeconds;
    var sessionState = sessionSource?.Poll(elapsedSeconds) ?? SmokeSession.Describe(smoke, frame);
    var badgeLayout = badge.Update(sessionState, elapsedSeconds, DateTimeOffset.UtcNow, input.MouseX, input.MouseY, input.WasPressed(Key.F4), renderer.Width, renderer.Height);
    if (badgeLayout is not null)
    {
        SessionBadge.Draw(sprites, badgeLayout);
    }

    // Shot mode: wait until every wanted chunk is on the GPU, let a few frames pass, then take one picture and stop.
    if (options.ShotPath is not null && world is not null)
    {
        settledFrames = world.Manager.PendingCount == 0 && world.Manager.LoadedCount > 0 ? settledFrames + 1 : 0;
        if (shotRequestedAt < 0 && settledFrames >= 8)
        {
            renderer.RequestCapture(options.ShotPath);
            shotRequestedAt = frame;
        }
    }
    else if (options.CapturePath is not null && (options.Frames > 0 && frame == options.Frames - 1 || smoke?.WantsCapture(frame) == true))
    {
        renderer.RequestCapture(options.CapturePath);
    }

    if (benchmark is not null && world is not null)
    {
        benchmarkObservation = (world.Manager.PendingCount == 0 && world.Manager.LoadedCount > 0, seconds, updateMilliseconds);
    }

    var renderStart = Stopwatch.GetTimestamp();
    bool drew;
    try
    {
        drew = renderer.Render(sprites, SkyColors.Encode(sun.FogColor), scene);
    }
    catch (VulkanException ex)
    {
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"RENDER FAILED at frame {frame}, {Stopwatch.GetElapsedTime(runStart).TotalSeconds:F2} s: {ex.Message}"));
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  chunks {world?.Manager.LoadedCount}, loading {world?.Manager.PendingCount}, uploaded this frame {world?.Manager.UploadedLastUpdate}, arena vertices {renderer.ArenaVerticesInUse}, visible sections {renderer.VisibleSectionCount}, camera {camera.Position}"));
        return 2;
    }
    if (benchmark is not null)
    {
        benchmark.Observe(frame, benchmarkObservation.Loaded, benchmarkObservation.Seconds, benchmarkObservation.Update, Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
    }

    if (drew)
    {
        rendered++;
    }
    else
    {
        skipped++;
    }

    if (options.MaxFps > 0)
    {
        // Wait out the rest of this frame's time slice: sleep for most of it, then spin for the last fraction for accuracy.
        var slice = TimeSpan.FromSeconds(1.0 / options.MaxFps);
        while (Stopwatch.GetElapsedTime(now) < slice)
        {
            if (slice - Stopwatch.GetElapsedTime(now) > TimeSpan.FromMilliseconds(2))
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    frame++;
    if (options.Frames > 0 && frame >= options.Frames)
    {
        break;
    }

    if (smoke?.Finished == true || (shotRequestedAt >= 0 && frame > shotRequestedAt) || benchmark?.Finished == true)
    {
        break;
    }

    if (options.ShotPath is not null && Stopwatch.GetElapsedTime(runStart).TotalSeconds > 120)
    {
        Console.Error.WriteLine("SHOT FAILED: the world did not settle within 120 seconds.");
        return 1;
    }
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Zombies.Client: {rendered} frames drawn, {skipped} skipped, {stats}, {renderer.Width}x{renderer.Height} on {renderer.DeviceName} ({renderer.PresentationInfo})"));
if (world is not null)
{
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"  world: {world.Manager.LoadedCount} chunks loaded, {world.Manager.PendingCount} loading, {renderer.ArenaVerticesInUse} vertices in the arena, {renderer.ArenaFailures} arena failures, {world.WorkerCount} workers"));
}

if (benchmark is not null)
{
    return benchmark.Report(options.BudgetMilliseconds);
}

if (smoke is not null)
{
    return smoke.Report(rendered, renderer.Width, renderer.Height);
}

return options.ShotPath is not null && shotRequestedAt < 0 ? 1 : 0;

/// <summary>The other players in the replicated world, as bodies to draw. The local player is left out; the camera is their body.</summary>
static IReadOnlyList<BodyInstance> RemoteBodies(WorldSession world, ClientOptions options, Camera camera)
{
    var client = world.Solo.Client;
    var bodies = new List<BodyInstance>();
    foreach (ref readonly var entity in client.World.Entities)
    {
        if (entity.Id != client.PlayerEntityId && entity.Kind == EntityKind.Player)
        {
            bodies.Add(new BodyInstance(entity.Position, entity.Yaw, false));
        }
    }

    // Diagnostic: stand-in bodies in front of the camera, so the body render path can be checked without a second player.
    for (var i = 0; i < options.DummyBodies; i++)
    {
        var forward = new Vector3(MathF.Sin(camera.Yaw), 0, -MathF.Cos(camera.Yaw));
        var right = new Vector3(MathF.Cos(camera.Yaw), 0, MathF.Sin(camera.Yaw));
        var feet = camera.Position with { Y = camera.Position.Y - PlayerMovement.EyeHeight };
        bodies.Add(new BodyInstance(feet + (forward * 4f) + (right * ((i - (options.DummyBodies / 2f)) * 1.2f)), camera.Yaw + MathF.PI, false));
    }

    return bodies;
}
