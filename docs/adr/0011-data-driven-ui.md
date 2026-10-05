# 11. Data-driven UI with a retained widget tree

- Status: Accepted
- Date: 2026-02-14

## Context

The game needs an inventory screen with drag and drop, a HUD, a body-part health screen, and an options
screen, in English and Swedish, with key remapping, a UI scale, and a colorblind palette. Mods must be able
to add or replace screens, and the UI must stay readable on a low-end machine with only a 5 by 7 bitmap font
and no font shaping, no vector text, and no layout engine.

The alternatives were an immediate-mode UI drawn from code each frame, a third-party UI library, or a
retained tree described as data.

## Decision

Screens are **data**: a JSON layout is a tree of widgets, each with a type, an optional id, an anchor, an
offset, an optional size, a color role, and string table keys. `UiLayout.TryParse` validates the tree and
refuses a bad one with the path to the problem, so a mod author is told where they went wrong. `Arrange`
places the tree against the screen and a scale; `HitTest` finds the deepest widget under a point, resolving
overlapping siblings to the one drawn last.

What a screen **shows** is kept apart from what it **is**: a `UiValues` holds the text, bar fill, color role,
and visibility of each widget by id. The layout is parsed once and arranged only when the screen size or the
scale changes; the values change every frame. This is what makes the same layout usable for a HUD that
updates sixty times a second and for a dialog that never changes.

Text is looked up by key through a `Localizer`, which merges the string tables of the loaded mods in load
order. A key the chosen language lacks falls back to English, and a key nobody has shows as the key itself,
so a missing translation is visible instead of blank. A test asserts that Swedish translates every key
English has, uses the same placeholders, and only uses characters the bitmap font can draw.

Colors are asked for by **role**, never by value, so the palette can change without touching a layout. The
colorblind palette uses the Okabe-Ito set, whose blue, yellow, and vermillion differ in lightness and in the
blue-yellow axis, which stays visible with red-green color blindness. A test simulates deuteranopia and
measures CIE Lab distance to prove the palette really is needed and really does help.

Input goes through **game actions**, never raw keys, so the player can remap them. Every action has exactly
one key and no key serves two actions, so a press is never ambiguous; Escape is reserved for closing screens.

## Consequences

- A mod can add a screen or replace one of the same id by dropping a file in `ui/`, with no code.
- The layout is validated once, so a bad one is reported at load with the path to the problem rather than
  failing at draw time.
- The bitmap font limits text to capitals and a fixed set of punctuation. Swedish Å, Ä, and Ö were added to
  the font; a language needing more than that would need a real font.
- The UI is drawn through the existing sprite batch, so it costs no new GPU state and works on the same
  low-end target as the rest of the renderer.
- A screen that needs something the widget kinds do not cover, such as a scrollable list, has to be drawn by
  code over the layout rather than described in it.
