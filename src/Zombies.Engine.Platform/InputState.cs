namespace Zombies.Engine.Platform;

/// <summary>Keys the game cares about. Values are the USB HID usage numbers SDL uses, so they are stable across platforms and layouts.</summary>
public enum Key
{
    Unknown = 0,
    A = 4,
    B = 5,
    C = 6,
    D = 7,
    E = 8,
    F = 9,
    G = 10,
    H = 11,
    I = 12,
    J = 13,
    K = 14,
    L = 15,
    M = 16,
    N = 17,
    O = 18,
    P = 19,
    Q = 20,
    R = 21,
    S = 22,
    T = 23,
    U = 24,
    V = 25,
    W = 26,
    X = 27,
    Y = 28,
    Z = 29,
    Digit1 = 30,
    Digit2 = 31,
    Digit3 = 32,
    Digit4 = 33,
    Digit5 = 34,
    Digit6 = 35,
    Digit7 = 36,
    Digit8 = 37,
    Digit9 = 38,
    Digit0 = 39,
    Enter = 40,
    Escape = 41,
    Backspace = 42,
    Tab = 43,
    Space = 44,
    F1 = 58,
    F2 = 59,
    F3 = 60,
    F4 = 61,
    F5 = 62,
    F6 = 63,
    F7 = 64,
    F8 = 65,
    F9 = 66,
    F10 = 67,
    F11 = 68,
    F12 = 69,
    Right = 79,
    Left = 80,
    Down = 81,
    Up = 82,
    LeftCtrl = 224,
    LeftShift = 225,
    LeftAlt = 226,
}

public enum MouseButton
{
    Left = 1,
    Middle = 2,
    Right = 3,
}

/// <summary>
/// What the player is holding down and where the mouse is. The window feeds it events; the game reads it.
/// "Pressed" and "released" last for exactly one frame, from one <see cref="BeginFrame"/> to the next.
/// </summary>
public sealed class InputState
{
    private const int KeyCount = 512;

    private readonly bool[] _down = new bool[KeyCount];
    private readonly bool[] _pressed = new bool[KeyCount];
    private readonly bool[] _released = new bool[KeyCount];
    private readonly bool[] _mouseDown = new bool[8];
    private readonly bool[] _mousePressed = new bool[8];

    public float MouseX { get; private set; }

    public float MouseY { get; private set; }

    /// <summary>Mouse movement since the last <see cref="BeginFrame"/>. In relative mode this is the look delta.</summary>
    public float MouseDeltaX { get; private set; }

    public float MouseDeltaY { get; private set; }

    public bool IsDown(Key key) => InRange(key) && _down[(int)key];

    public bool WasPressed(Key key) => InRange(key) && _pressed[(int)key];

    public bool WasReleased(Key key) => InRange(key) && _released[(int)key];

    public bool IsDown(MouseButton button) => _mouseDown[(int)button];

    public bool WasPressed(MouseButton button) => _mousePressed[(int)button];

    /// <summary>Clears the one-frame state. Call once per frame, before pumping window events.</summary>
    public void BeginFrame()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
        Array.Clear(_mousePressed);
        MouseDeltaX = 0;
        MouseDeltaY = 0;
    }

    /// <summary>Forgets everything held, for example when the window loses focus so no key stays stuck down.</summary>
    public void ReleaseAll()
    {
        for (var i = 0; i < KeyCount; i++)
        {
            if (_down[i])
            {
                _down[i] = false;
                _released[i] = true;
            }
        }

        Array.Clear(_mouseDown);
    }

    public void SetKey(int scancode, bool down, bool isRepeat = false)
    {
        if ((uint)scancode >= KeyCount || isRepeat)
        {
            return;
        }

        if (down && !_down[scancode])
        {
            _pressed[scancode] = true;
        }
        else if (!down && _down[scancode])
        {
            _released[scancode] = true;
        }

        _down[scancode] = down;
    }

    public void SetMouseButton(int button, bool down)
    {
        if ((uint)button >= _mouseDown.Length)
        {
            return;
        }

        if (down && !_mouseDown[button])
        {
            _mousePressed[button] = true;
        }

        _mouseDown[button] = down;
    }

    public void MoveMouse(float x, float y, float deltaX, float deltaY)
    {
        MouseX = x;
        MouseY = y;
        MouseDeltaX += deltaX;
        MouseDeltaY += deltaY;
    }

    private static bool InRange(Key key) => (uint)(int)key < KeyCount;
}
