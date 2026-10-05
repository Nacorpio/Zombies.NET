using Zombies.Engine.Platform;

namespace Zombies.Engine.Ui;

/// <summary>Things the player can do with a key. The game reads input through these, never through raw keys, so the player can remap them.</summary>
public enum GameAction
{
    MoveForward,
    MoveBack,
    StrafeLeft,
    StrafeRight,
    Jump,
    Sprint,
    Crouch,
    LeanLeft,
    LeanRight,
    Inventory,
    BodyScreen,
    ToggleMouse,
    Interact,
}

/// <summary>
/// Which key does each <see cref="GameAction"/>. Every action has exactly one key and no key serves two actions, so a
/// press is never ambiguous. Escape is reserved for closing screens and cannot be bound.
/// </summary>
public sealed class KeyBindings
{
    private static readonly IReadOnlyDictionary<GameAction, Key> DefaultKeys = new Dictionary<GameAction, Key>
    {
        [GameAction.MoveForward] = Key.W,
        [GameAction.MoveBack] = Key.S,
        [GameAction.StrafeLeft] = Key.A,
        [GameAction.StrafeRight] = Key.D,
        [GameAction.Jump] = Key.Space,
        [GameAction.Sprint] = Key.LeftShift,
        [GameAction.Crouch] = Key.LeftCtrl,
        [GameAction.LeanLeft] = Key.Q,
        [GameAction.LeanRight] = Key.E,
        [GameAction.Inventory] = Key.I,
        [GameAction.BodyScreen] = Key.H,
        [GameAction.ToggleMouse] = Key.Tab,
        [GameAction.Interact] = Key.F,
    };

    private readonly Dictionary<GameAction, Key> _keys;

    private KeyBindings(Dictionary<GameAction, Key> keys) => _keys = keys;

    public static KeyBindings Defaults() => new(new Dictionary<GameAction, Key>(DefaultKeys));

    /// <summary>Builds bindings from the defaults with <paramref name="overrides"/> applied, or says why they cannot all hold at once.</summary>
    public static bool TryCreate(IReadOnlyDictionary<GameAction, Key> overrides, out KeyBindings bindings, out string error)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var keys = new Dictionary<GameAction, Key>(DefaultKeys);
        foreach (var (action, key) in overrides)
        {
            if (!IsBindable(key))
            {
                bindings = null!;
                error = $"{key} cannot be bound to {action}.";
                return false;
            }

            keys[action] = key;
        }

        foreach (var group in keys.GroupBy(k => k.Value).Where(g => g.Count() > 1))
        {
            bindings = null!;
            error = $"{group.Key} is bound to both {string.Join(" and ", group.Select(g => g.Key).Order())}.";
            return false;
        }

        bindings = new KeyBindings(keys);
        error = string.Empty;
        return true;
    }

    public Key KeyFor(GameAction action) => _keys[action];

    public GameAction? ActionFor(Key key) => _keys.Where(k => k.Value == key).Select(k => (GameAction?)k.Key).FirstOrDefault();

    /// <summary>Moves an action to a key. Fails, changing nothing, when another action already uses the key; <paramref name="conflict"/> names it.</summary>
    public bool Rebind(GameAction action, Key key, out GameAction? conflict)
    {
        conflict = null;
        if (!IsBindable(key))
        {
            return false;
        }

        if (ActionFor(key) is { } holder && holder != action)
        {
            conflict = holder;
            return false;
        }

        _keys[action] = key;
        return true;
    }

    /// <summary>Moves an action to a key, and gives the action that held the key this action's old key, so neither is left unbound.</summary>
    public bool RebindSwapping(GameAction action, Key key)
    {
        if (!IsBindable(key))
        {
            return false;
        }

        if (ActionFor(key) is { } holder && holder != action)
        {
            _keys[holder] = _keys[action];
        }

        _keys[action] = key;
        return true;
    }

    public void Reset()
    {
        foreach (var (action, key) in DefaultKeys)
        {
            _keys[action] = key;
        }
    }

    public KeyBindings Clone() => new(new Dictionary<GameAction, Key>(_keys));

    public bool IsDown(InputState input, GameAction action)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.IsDown(KeyFor(action));
    }

    public bool WasPressed(InputState input, GameAction action)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.WasPressed(KeyFor(action));
    }

    private static bool IsBindable(Key key) => key is not (Key.Unknown or Key.Escape);
}
