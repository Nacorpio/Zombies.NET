using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Ui;

namespace Zombies.Client;

/// <summary>Which screen the player is looking at. The world is always behind them.</summary>
internal enum UiScreen
{
    None,
    Inventory,
    Body,
    Options,
}

/// <summary>
/// The player's own state as the HUD reads it: their Body, their Needs, and the weapon in hand. The Server owns the
/// authoritative copy; this is the local view of it, which is what the HUD draws.
/// </summary>
internal sealed class PlayerStatus
{
    public Body Body { get; } = new(new BodyId(1));

    public Needs Needs { get; } = new();

    public ItemId? Weapon { get; set; }

    public ItemState? WeaponState { get; set; }
}

/// <summary>
/// Everything the client's screens need: the loaded language, the layouts, the settings, and the models the HUD and the
/// inventory read. It owns the screens' input and drawing so the main loop only has to call <see cref="Update"/> and
/// <see cref="Draw"/>.
/// </summary>
internal sealed class UiSession : IDisposable
{
    private readonly Localizer _localizer;
    private readonly UiValues _values = new();
    private readonly UiLayout _hud;
    private readonly UiLayout _inventoryLayout;
    private readonly UiLayout _body;
    private readonly UiLayout _options;
    private readonly InventoryView _inventoryView;
    private readonly PlayerStatus _status;
    private readonly InventoryService _inventoryService;
    private readonly InMemoryContainerRepository _containers;
    private readonly ItemCatalog _catalog;
    private readonly KeyBindings _keys;
    private readonly SettingsEditor _editor;
    private readonly List<Dialog> _dialogs = [];

    private UiScreen _screen;
    private UiRect _screenRect;
    private UiContext _context = new(new Localizer([]), 1f, 1);
    private int _textScale = 1;
    private bool _mouseWasDown;

    public UiSession(ClientOptions options, PlayerStatus status)
    {
        var packages = DirectoryModSource.Read(options.ModsDirectory ?? DirectoryModSource.Find(AppContext.BaseDirectory));
        var mods = ModLoader.Load(packages);
        if (!mods.IsSuccess)
        {
            throw new InvalidOperationException("The mods could not be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, mods.Errors));
        }

        var localization = LocalizationLoader.Load(packages, mods);
        foreach (var problem in localization.Problems)
        {
            Console.Error.WriteLine(problem);
        }

        var layouts = LayoutLoader.Load(packages, mods);
        foreach (var problem in layouts.Problems)
        {
            Console.Error.WriteLine(problem);
        }

        _localizer = localization.Localizer;
        _hud = layouts.Get("hud") ?? throw new InvalidOperationException("No 'hud' layout was loaded.");
        _inventoryLayout = layouts.Get("inventory") ?? throw new InvalidOperationException("No 'inventory' layout was loaded.");
        _body = layouts.Get("body") ?? throw new InvalidOperationException("No 'body' layout was loaded.");
        _options = layouts.Get("options") ?? throw new InvalidOperationException("No 'options' layout was loaded.");

        Settings = new GameSettings();
        _editor = new SettingsEditor(Settings);
        _editor.Changed += () =>
        {
            Settings = _editor.Current;
            _localizer.Language = Settings.Language;
        };
        _keys = Settings.Keys;
        _status = status;

        _catalog = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        _containers = new InMemoryContainerRepository();
        _inventoryService = new InventoryService(_catalog, _containers);
        _inventoryView = new InventoryView(_inventoryService, _containers, _catalog, _localizer);

        // A backpack and the ground, filled with a few things so the screen has something to show.
        var backpack = new ContainerId(1);
        var ground = new ContainerId(2);
        _containers.TryAdd(new Container(backpack, UnitsNet.Mass.FromKilograms(20), UnitsNet.Volume.FromLiters(30), _catalog));
        _containers.TryAdd(new Container(ground, UnitsNet.Mass.FromKilograms(1000), UnitsNet.Volume.FromLiters(1000), _catalog));
        _inventoryView.AddTarget(backpack, "inv.backpack");
        _inventoryView.AddTarget(ground, "inv.ground");
        Backpack = backpack;
        Ground = ground;
        SeedInventory();
    }

    public GameSettings Settings { get; private set; }

    public ContainerId Backpack { get; }

    public ContainerId Ground { get; }

    public UiScreen Screen => _screen;

    /// <summary>Opens a screen by name, for a diagnostic run that wants to photograph it.</summary>
    public bool Open(string name)
    {
        switch (name)
        {
            case "inventory": _screen = UiScreen.Inventory; return true;
            case "body": _screen = UiScreen.Body; return true;
            case "options": _screen = UiScreen.Options; return true;
            case "dialog":
                _screen = UiScreen.Inventory;
                Ask("dialog.drop_all.title", "dialog.drop_all.message", "dialog.drop_all.caption", new DialogButton("yes", "dialog.drop_all.yes"), new DialogButton("no", "dialog.drop_all.no", IsCancel: true));
                return true;
            case "none": _screen = UiScreen.None; return true;
            default: return false;
        }
    }

    public bool IsOpen => _screen != UiScreen.None;

    /// <summary>Whether the mouse should be free for the screens to use.</summary>
    public bool WantsMouse => IsOpen;

    public void Dispose()
    {
    }

    /// <summary>Handles the keys and clicks the screens use. Returns true when the screens took the input, so the world should not also act on it.</summary>
    public bool Update(InputState input, float seconds)
    {
        if (_dialogs.Count > 0)
        {
            var dialog = _dialogs[^1];
            if (input.WasPressed(Key.Enter) || input.WasPressed(Key.Space))
            {
                dialog.Activate();
            }
            else if (input.WasPressed(Key.Escape))
            {
                dialog.Cancel();
            }
            else if (input.WasPressed(Key.Tab) || input.WasPressed(Key.Down))
            {
                dialog.MoveFocus(+1);
            }
            else if (input.WasPressed(Key.Up))
            {
                dialog.MoveFocus(-1);
            }
            else if (input.WasPressed(MouseButton.Left))
            {
                dialog.Click(input.MouseX, input.MouseY);
            }

            if (!dialog.IsOpen)
            {
                _dialogs.Remove(dialog);
                if (dialog.Result == "yes")
                {
                    DropEverything();
                }
            }

            return true;
        }

        if (input.WasPressed(Key.Escape) && IsOpen)
        {
            _screen = UiScreen.None;
            return true;
        }

        if (_keys.WasPressed(input, GameAction.Inventory))
        {
            _screen = _screen == UiScreen.Inventory ? UiScreen.None : UiScreen.Inventory;
            return true;
        }

        if (_keys.WasPressed(input, GameAction.BodyScreen))
        {
            _screen = _screen == UiScreen.Body ? UiScreen.None : UiScreen.Body;
            return true;
        }

        if (input.WasPressed(Key.F5))
        {
            _screen = _screen == UiScreen.Options ? UiScreen.None : UiScreen.Options;
            return true;
        }

        if (_screen == UiScreen.Inventory)
        {
            UpdateInventory(input);
            return true;
        }
        if (_screen == UiScreen.Options)
        {
            UpdateOptions(input);
            return true;
        }

        return IsOpen;
    }

    /// <summary>Draws the HUD and, when one is open, the screen over it.</summary>
    public void Draw(SpriteBatch sprites, int width, int height)
    {
        _screenRect = new UiRect(0, 0, width, height);
        _textScale = Math.Max(1, (int)MathF.Round(Settings.UiScale));
        _context = new UiContext(_localizer, Settings.UiScale, _textScale);
        var palette = UiPalette.For(Settings.Palette);

        _hud.Arrange(_screenRect, _context);
        FillHudValues();
        UiRenderer.Draw(sprites, _hud, _values, _context, palette);

        switch (_screen)
        {
            case UiScreen.Inventory:
                DrawInventory(sprites, palette);
                break;
            case UiScreen.Body:
                DrawBody(sprites, palette);
                break;
            case UiScreen.Options:
                DrawOptions(sprites, palette);
                break;
        }

        foreach (var dialog in _dialogs)
        {
            UiRenderer.DrawDialog(sprites, dialog, palette, _textScale);
        }
    }

    /// <summary>Asks a question and waits for the answer. The dialog takes every click until it closes.</summary>
    public void Ask(string titleKey, string messageKey, string captionKey, params DialogButton[] buttons)
    {
        var dialog = Dialog.Create(_localizer, titleKey, messageKey, captionKey, buttons);
        dialog.Arrange(_screenRect.Width > 0 ? _screenRect : new UiRect(0, 0, 1280, 720), _textScale);
        _dialogs.Add(dialog);
    }

    private void UpdateInventory(InputState input)
    {
        // Dropping everything is the one action that asks first, since it cannot be undone.
        if (input.WasPressed(Key.G))
        {
            Ask("dialog.drop_all.title", "dialog.drop_all.message", "dialog.drop_all.caption", new DialogButton("yes", "dialog.drop_all.yes"), new DialogButton("no", "dialog.drop_all.no", IsCancel: true));
            return;
        }

        if (input.WasPressed(MouseButton.Left))
        {
            _mouseWasDown = true;
            var panel = _inventoryView.Panel(Backpack);
            var slots = _inventoryView.ArrangeSlots(Backpack, _inventoryLayout.Find("inv.backpack")!.Bounds, _textScale);
            for (var i = 0; i < slots.Count && i < panel.Slots.Count; i++)
            {
                if (slots[i].Contains(input.MouseX, input.MouseY))
                {
                    _inventoryView.BeginDrag(Backpack, panel.Slots[i].Stack);
                    return;
                }
            }
        }

        // The input state has no mouse-release flag, so the drop happens on the frame the button stops being held.
        if (_mouseWasDown && !input.IsDown(MouseButton.Left))
        {
            _mouseWasDown = false;
            if (_inventoryView.Dragging is not null)
            {
                var ground = _inventoryLayout.Find("inv.ground")!.Bounds;
                _inventoryView.DropOn(ground.Contains(input.MouseX, input.MouseY) ? Ground : Backpack);
            }
        }
    }

    /// <summary>Moves every Stack of the backpack to the ground, one command at a time, so a Stack that does not fit stays put.</summary>
    private void DropEverything()
    {
        foreach (var slot in _inventoryView.Slots(Backpack))
        {
            _inventoryView.BeginDrag(Backpack, slot.Stack);
            _inventoryView.DropOn(Ground);
        }
    }

    private void UpdateOptions(InputState input)
    {
        if (input.WasPressed(Key.Left) || input.WasPressed(Key.Right))
        {
            var step = input.WasPressed(Key.Right) ? 1 : -1;
            if (input.IsDown(Key.LeftShift))
            {
                _editor.FieldOfView.Nudge(step);
            }
            else
            {
                _editor.UiScale.Nudge(step);
            }
        }

        if (input.WasPressed(Key.B))
        {
            _editor.ToggleHeadBob();
        }

        if (input.WasPressed(Key.C))
        {
            _editor.NextPalette();
        }

        if (input.WasPressed(Key.G))
        {
            _editor.NextGore();
        }

        if (input.WasPressed(Key.L))
        {
            var languages = _localizer.Languages;
            var next = (languages.ToList().IndexOf(_localizer.Language) + 1) % languages.Count;
            _editor.SetLanguage(languages[next]);
        }

        if (input.WasPressed(Key.R))
        {
            _keys.Reset();
        }
    }

    private void FillHudValues()
    {
        var hud = new HudModel(_status.Body, _status.Needs, _status.Weapon, _status.WeaponState, _localizer);
        _values.SetFraction("hud.health", (float)hud.HealthFraction);
        _values.SetRole("hud.health", hud.HealthRole);
        _values.SetFraction("hud.blood", (float)hud.BloodFraction);
        _values.SetRole("hud.blood", hud.BloodRole);
        _values.SetVisible("hud.bleeding", hud.IsBleeding);
        _values.SetText("hud.bleeding", _localizer.Get("hud.bleeding"));
        _values.SetText("hud.weapon", hud.WeaponLabel);
        _values.SetText("hud.ammo", hud.Ammo is { } ammo ? $"{_localizer.Get("hud.ammo")} {ammo}" : string.Empty);
        _values.SetVisible("hud.ammo", hud.Ammo is not null);
        _values.SetFraction("hud.condition", (float)hud.ConditionFraction);
        _values.SetRole("hud.condition", hud.ConditionRole);
        _values.SetVisible("hud.condition", _status.Weapon is not null);
        _values.SetText("hud.hunger", hud.HungerLabel);
        _values.SetRole("hud.hunger", hud.HungerRole);
        _values.SetText("hud.thirst", hud.ThirstLabel);
        _values.SetRole("hud.thirst", hud.ThirstRole);
        _values.SetText("hud.warmth", hud.WarmthLabel);
        _values.SetRole("hud.warmth", hud.WarmthRole);
    }

    private void DrawInventory(SpriteBatch sprites, UiPalette palette)
    {
        _inventoryLayout.Arrange(_screenRect, _context);
        UiRenderer.Draw(sprites, _inventoryLayout, _values, _context, palette);

        DrawSlots(sprites, palette, Backpack, _inventoryLayout.Find("inv.backpack")!.Bounds);
        DrawSlots(sprites, palette, Ground, _inventoryLayout.Find("inv.ground")!.Bounds);

        if (_inventoryView.Message is { } message)
        {
            sprites.DrawText(message, _screenRect.X + (8 * _textScale), _screenRect.Bottom - (24 * _textScale), _textScale, palette.Color(PaletteRole.Danger));
        }
    }

    private void DrawSlots(SpriteBatch sprites, UiPalette palette, ContainerId container, UiRect panel)
    {
        var slots = _inventoryView.Slots(container);
        var rects = _inventoryView.ArrangeSlots(container, panel, _textScale);
        for (var i = 0; i < rects.Count && i < slots.Count; i++)
        {
            var slot = slots[i];
            var rect = rects[i];
            var dragging = _inventoryView.Dragging is { } dragged && dragged.Stack == slot.Stack && dragged.From == container;
            sprites.FillRect(rect.X, rect.Y, rect.Width, rect.Height, palette.Color(dragging ? PaletteRole.Info : PaletteRole.Muted));
            var label = slot.Count > 1 ? $"{slot.Label} x{slot.Count}" : slot.Label;
            sprites.DrawText(label, rect.X + (2 * _textScale), rect.Y + (2 * _textScale), _textScale, palette.Color(PaletteRole.Text));
        }
    }

    private void DrawBody(SpriteBatch sprites, UiPalette palette)
    {
        _body.Arrange(_screenRect, _context);
        var hud = new HudModel(_status.Body, _status.Needs, _status.Weapon, _status.WeaponState, _localizer);
        foreach (var part in hud.Parts)
        {
            var id = $"body.part.{Snake(part.Part.ToString())}";
            _values.SetText(id, part.StateLabel is { } state ? $"{part.Label} {state}" : part.Label);
            _values.SetRole(id, part.Role);
        }

        UiRenderer.Draw(sprites, _body, _values, _context, palette);
    }

    private void DrawOptions(SpriteBatch sprites, UiPalette palette)
    {
        _options.Arrange(_screenRect, _context);
        _values.SetText("options.language", $"{_localizer.Get("options.language")}: {_localizer.Language}");
        _values.SetText("options.ui_scale", $"{_localizer.Get("options.ui_scale")}: {Settings.UiScale:0.00}");
        _values.SetText("options.field_of_view", $"{_localizer.Get("options.field_of_view")}: {Settings.FieldOfViewDegrees:0}");
        _values.SetText("options.head_bob", $"{_localizer.Get("options.head_bob")}: {_localizer.Get(Settings.HeadBob ? "options.on" : "options.off")}");
        _values.SetText("options.palette", $"{_localizer.Get("options.palette")}: {_localizer.Get($"options.palette.{Settings.Palette.ToString().ToLowerInvariant()}")}");
        _values.SetText("options.gore", $"{_localizer.Get("options.gore")}: {_localizer.Get($"options.gore.{Settings.Gore.ToString().ToLowerInvariant()}")}");
        _values.SetText("options.keys", $"{_localizer.Get("options.keys")}: {_keys.KeyFor(GameAction.Inventory)}");
        UiRenderer.Draw(sprites, _options, _values, _context, palette);
    }

    private void SeedInventory()
    {
        foreach (var (item, count) in new[]
        {
            ("base:item/canned_beans", 3),
            ("base:item/bandage", 4),
            ("base:item/water_bottle", 1),
            ("base:item/crowbar", 1),
        })
        {
            if (ItemId.TryParse(item, out var id) && _catalog.TryGet(id, out _))
            {
                _inventoryService.AddItems(Backpack, id, count);
            }
        }

        if (ItemId.TryParse("base:item/pistol_9mm", out var pistol) && _catalog.TryGet(pistol, out _))
        {
            var state = ItemState.Create([new(WeaponService.ConditionValue, 72), new(WeaponService.RoundsValue, 9)]);
            _inventoryService.AddItems(Backpack, pistol, 1, state);
            _status.Weapon = pistol;
            _status.WeaponState = state;
        }
    }

    private static string Snake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
