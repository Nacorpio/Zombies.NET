using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Zombies.Client;
using Zombies.Domain.Zombies;
using Zombies.Engine.Core.Debugging;
using Zombies.Engine.Net;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Render.Vulkan;
using Zombies.Engine.Ui;

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
//   --screen NAME     open a screen at startup (inventory, inventory-menu, inventory-menu-ground, body, options, dialog) so a screenshot can show it
//   --smoke           scripted resize / minimize / restore / input check; exit code 0 means it all worked
// Interactive: WASD fly, Space and Ctrl up and down, Shift fast, Tab mouse look, F1 overlay, F2 pause time, F3 day speed, Left/Right arrows change time.
var options = ClientOptions.Parse(args);
var width = options.Benchmark && !options.SizeSpecified ? 1920 : options.Width;
var height = options.Benchmark && !options.SizeSpecified ? 1080 : options.Height;

IconSetup.Install(options);

using var window = new SdlWindow("Zombies.NET", width, height);
using var renderer = new VulkanRenderer(window, new RendererOptions(!options.NoVSync, options.ShadowCascades, options.ShadowResolution));
WorldSession? world;
try
{
    world = options.NoWorld ? null : new WorldSession(options, renderer);
}
catch (JoinRefusedException refused)
{
    // A refused join is shown to the player, not just logged, so they know why they cannot play.
    Console.Error.WriteLine($"Zombies.Client: {refused.Message}");
    return 3;
}

using var worldScope = world;

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
using var ui = options.NoWorld ? null : new UiSession(options, new PlayerStatus());
if (ui is not null && options.Screen is { } startScreen && !ui.Open(startScreen))
{
    throw new ArgumentException($"Unknown screen '{startScreen}'. Use inventory, inventory-menu, inventory-menu-ground, body, options, dialog, or none.");
}

// The inventory screen's changes go to the Server as commands; the Server's answer comes back as a new Container view.
if (ui is not null && world is not null)
{
    ui.Used += used => world.Client.Send(new UseItem(used.Item));
    ui.MoveRequested += (from, stack, to, count) => world.Client.Send(new MoveStackCommand(from, stack, to, count));
    ui.SplitRequested += (container, stack, count) => world.Client.Send(new SplitStackCommand(container, stack, count));
    ui.DropRequested += (container, stack, count) => world.Client.Send(new DropStackCommand(container, stack, count));
    ui.OpenRequested += (x, y, z) => world.Client.Send(new OpenContainer(x, y, z));
    ui.HoldRequested += item => world.Client.Send(new HoldWeapon(item));
    ui.TreatRequested += (part, treatment) => world.Client.Send(new TreatWounds(part, treatment));
}

var inputClock = new Zombies.Engine.Core.FixedStepClock();
var dayLengths = new[] { 24f * 60f, 120f, 20f };
var dayLength = 0;
var showOverlay = true;

// Playing starts with the mouse looking around, so the player does not have to find Tab first; a screenshot, benchmark or smoke run keeps the cursor free.
var mouseCaptured = world is not null && smoke is null && benchmark is null && options.ShotPath is null && !(ui?.IsOpen ?? false);
if (mouseCaptured)
{
    window.SetRelativeMouse(true);
}

// Where the player is looking. Mouse movement lands here, not on the camera, because the camera is placed at the predicted
// player every frame and a frame that owes no input command would otherwise throw the turn away.
var aim = new PlayerAim(world?.Client.Local.State.Yaw ?? camera.Yaw, world?.Client.Local.State.Pitch ?? camera.Pitch);

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

    // The screens take their input first, so a key that opens one does not also move the player.
    var uiTookInput = ui?.Update(input, seconds) ?? false;
    if (ui is not null)
    {
        window.SetRelativeMouse(!ui.WantsMouse && mouseCaptured);
    }

    if (input.WasPressed(Key.F2))
    {
        clock.Paused = !clock.Paused;
    }

    // Interact: open the world Container the player is looking at, if the Server has any.
    if (world is not null && ui is not null && !ui.IsOpen && input.WasPressed(Key.F))
    {
        var eye = PlayerMovement.EyePosition(world.Client.Local.State);
        var yaw = world.Client.Local.State.Yaw;
        var pitch = world.Client.Local.State.Pitch;
        var direction = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        if (world.TargetedContainer(eye, direction) is { } target)
        {
            ui.RequestOpen(target.X, target.Y, target.Z);
        }
    }

    // Combat: the mouse fires the Held weapon, R reloads it, H holds the first weapon the Server says the player carries, B bandages.
    if (world is not null && ui is not null && !ui.IsOpen && mouseCaptured)
    {
        if (input.WasPressed(MouseButton.Left))
        {
            world.Client.Send(new UseWeapon());
        }

        if (input.WasPressed(Key.R))
        {
            world.Client.Send(new LoadWeapon());
        }

        if (input.WasPressed(Key.H))
        {
            ui.RequestHoldFirstWeapon();
        }

        if (input.WasPressed(Key.B))
        {
            ui.RequestBandage();
        }
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
        if (benchmark is null && !uiTookInput)
        {
            if (mouseCaptured)
            {
                aim.Turn(input.MouseDeltaX, input.MouseDeltaY);
            }

            var playerInput = PlayerController.Read(input, aim.Yaw, aim.Pitch, mouseCaptured);
            // One input command is one Server tick of movement, so send one per tick owed, not one per frame; at 144 fps a
            // command per frame would walk the player more than twice as fast as the Server's tick rate allows.
            for (var owed = inputClock.Advance(TimeSpan.FromSeconds(seconds)); owed > 0; owed--)
            {
                world.Client.SendInput(playerInput);
            }
        }

        world.Advance(TimeSpan.FromSeconds(seconds));
        ui?.SetEffects(world.Client.Effects);
        ui?.SetStatus(world.Client.Status);
        ui?.SetContainers(world.Client.Containers);
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
            PlayerController.ApplyToCamera(camera, world.Client.Local.State, world.Client.Local.Lean);
            camera.Face(aim);
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
            sprites.DrawText(world is null ? "WASD FLY  SPACE/CTRL UP/DOWN  SHIFT FAST  TAB MOUSE  F1 HUD  F2 TIME  F3 SPEED  ARROWS TIME" : "WASD WALK  SPACE JUMP  SHIFT SPRINT  CTRL CROUCH  TAB MOUSE  I BAG  F USE  F1 HUD  F2 TIME  F3 SPEED", 16, renderer.Height - 24, 2, new Rgba(200, 200, 200, 200));
        }
    }

    ui?.Draw(sprites, renderer.Width, renderer.Height);

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

/// <summary>The other players and the zombies in the replicated world, as bodies to draw. The local player is left out; the camera is their body.</summary>
static IReadOnlyList<BodyInstance> RemoteBodies(WorldSession world, ClientOptions options, Camera camera)
{
    var client = world.Client;
    var bodies = new List<BodyInstance>();
    foreach (ref readonly var entity in client.World.Entities)
    {
        if (entity.Id == client.PlayerEntityId)
        {
            continue;
        }

        if (entity.Kind == EntityKind.Player)
        {
            bodies.Add(new BodyInstance(entity.Position, entity.Yaw, false));
        }
        else if (entity.Kind == EntityKind.Zombie)
        {
            bodies.Add(ZombieBody(world, entity));
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

/// <summary>
/// A zombie as a body to draw. Its look is derived from the replicated spec, exactly as the Server derived it, so every client
/// draws the same zombie without the Server sending more than the spec, the missing parts, and whether it is dead.
/// </summary>
static BodyInstance ZombieBody(WorldSession world, in EntityState entity)
{
    return new BodyInstance(entity.Position, entity.Yaw, false);
}
