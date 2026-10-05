Ticket: M6: Context menu widget with icons

What to build: The UI toolkit gains a clean context menu: a short list of entries, each with an Icon, a label, and an optional shortcut hint, opened at the pointer with the secondary button. Entries can be enabled, disabled with a one-line reason shown on hover, or separated into groups. It stays inside the screen, closes on a click elsewhere or Escape, and works with mouse and keyboard.

Acceptance criteria:
- A menu has entries with Icon, localized label, optional shortcut hint, and enabled or disabled state with a reason
- Groups are separated by a thin divider; there are no borders, shadows, or decoration beyond the flat panel style of the rest of the UI
- Hovering a disabled entry shows its reason in a tooltip and does nothing when clicked
- The menu opens at the pointer and is moved to stay fully on screen at any window size and UI scale
- Up, Down, Enter, and Escape work; a click outside closes it; at most one menu is open at a time
- Text uses localization keys in English and Swedish, and the layout copes with longer Swedish labels
- Layout and navigation are tested without a GPU

Author's notes: Up and Down also land on disabled entries so the keyboard can reach their reason; Enter on a disabled entry does nothing. A click while a menu is open is consumed, including the click that closes it. When the menu does not fit the window the text scale drops toward 1; if it still does not fit it is shown from the top-left corner.
