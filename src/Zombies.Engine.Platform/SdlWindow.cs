using System.Runtime.InteropServices;
using SDL;

namespace Zombies.Engine.Platform;

/// <summary>A native window made with SDL3, created ready for Vulkan. It translates SDL events into <see cref="InputState"/> and window state.</summary>
public sealed unsafe class SdlWindow : IDisposable
{
    private SDL_Window* _window;
    private bool _disposed;

    public SdlWindow(string title, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            throw new InvalidOperationException($"SDL could not start: {SDL3.SDL_GetError()}");
        }

        _window = SDL3.SDL_CreateWindow(title, width, height, SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
        if (_window == null)
        {
            var error = SDL3.SDL_GetError();
            SDL3.SDL_Quit();
            throw new InvalidOperationException($"SDL could not create a window: {error}");
        }

        RefreshSize();
    }

    public InputState Input { get; } = new();

    /// <summary>Drawable size in pixels, which on high-DPI screens is larger than the window size.</summary>
    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public bool CloseRequested { get; private set; }

    public bool IsMinimized { get; private set; }

    /// <summary>True when the size changed during the last <see cref="PumpEvents"/>.</summary>
    public bool Resized { get; private set; }

    public bool HasFocus { get; private set; } = true;

    internal SDL_Window* Handle => _window;

    /// <summary>Processes every waiting event. Call once per frame; it also starts a new input frame.</summary>
    public void PumpEvents()
    {
        Input.BeginFrame();
        Resized = false;

        SDL_Event e;
        while (SDL3.SDL_PollEvent(&e))
        {
            switch ((SDL_EventType)e.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    CloseRequested = true;
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    IsMinimized = true;
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                    IsMinimized = false;
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    HasFocus = false;
                    Input.ReleaseAll();
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    HasFocus = true;
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                    RefreshSize();
                    Resized = true;
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    Input.SetKey((int)e.key.scancode, e.key.down, e.key.repeat);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    Input.MoveMouse(e.motion.x, e.motion.y, e.motion.xrel, e.motion.yrel);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    Input.SetMouseButton(e.button.button, e.button.down);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Queues a keyboard event through SDL itself, as if the key were pressed, so tests can prove the real event path works.</summary>
    public static void PushKeyEvent(Key key, bool down)
    {
        SDL_Event e = default;
        e.type = (uint)(down ? SDL_EventType.SDL_EVENT_KEY_DOWN : SDL_EventType.SDL_EVENT_KEY_UP);
        e.key.scancode = (SDL_Scancode)(int)key;
        e.key.down = down;
        if (!SDL3.SDL_PushEvent(&e))
        {
            throw new InvalidOperationException($"SDL could not queue an event: {SDL3.SDL_GetError()}");
        }
    }

    public void SetTitle(string title) => SDL3.SDL_SetWindowTitle(_window, title);

    public void SetSize(int width, int height) => SDL3.SDL_SetWindowSize(_window, width, height);

    public void Minimize() => SDL3.SDL_MinimizeWindow(_window);

    public void Restore() => SDL3.SDL_RestoreWindow(_window);

    /// <summary>Hides the cursor and reports raw movement, as a first-person camera needs.</summary>
    public void SetRelativeMouse(bool enabled) => SDL3.SDL_SetWindowRelativeMouseMode(_window, enabled);

    /// <summary>Instance extensions Vulkan needs to draw into this window.</summary>
    public static string[] VulkanInstanceExtensions()
    {
        uint count;
        var names = SDL3.SDL_Vulkan_GetInstanceExtensions(&count);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        return result;
    }

    /// <summary>Creates the Vulkan surface for this window. The caller owns it and must destroy it before the instance.</summary>
    public ulong CreateVulkanSurface(nint instance)
    {
        VkSurfaceKHR_T* surface;
        if (!SDL3.SDL_Vulkan_CreateSurface(_window, (VkInstance_T*)instance, null, &surface))
        {
            throw new InvalidOperationException($"SDL could not create a Vulkan surface: {SDL3.SDL_GetError()}");
        }

        return (ulong)surface;
    }

    public static void DestroyVulkanSurface(nint instance, ulong surface) =>
        SDL3.SDL_Vulkan_DestroySurface((VkInstance_T*)instance, (VkSurfaceKHR_T*)surface, null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SDL3.SDL_DestroyWindow(_window);
        _window = null;
        SDL3.SDL_Quit();
    }

    private void RefreshSize()
    {
        int width;
        int height;
        SDL3.SDL_GetWindowSizeInPixels(_window, &width, &height);
        PixelWidth = width;
        PixelHeight = height;
    }
}
