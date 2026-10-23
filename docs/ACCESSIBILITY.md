# Accessibility

The accessibility review of Phase 6, item 4. Each part lists what was checked, what was found, and what was fixed.

## Contrast and themes (done, D-046)

Target: WCAG 2.1 AA. Text needs 4.5:1 against the background it is drawn on; pins, wires, outlines, and icons need 3:1.

How it is checked: `tests/ScriptArt.UiTests/ThemeContrastTests.cs` computes the WCAG contrast ratio of every pair the IDE draws, in the dark and the light theme:

- Theme tokens (`Theme.axaml`): primary and secondary text on the window, panel, header, hover, and code backgrounds; error, warning, and success text; the accent; text on selected rows.
- Canvas (`CanvasTheme`): pin names, pin types, value previews, and the empty-graph hint on the node body or canvas; node titles on every category color; comment titles on the comment header; frame statistics on their box; data and execution pins on the node body and wires on the canvas; selection, error, and execution outlines.
- Code: every syntax color on the code background; added, removed, and header lines of the diff view.
- Icons in the Explorer and tabs on the window, panel, and header backgrounds.

Findings in the dark theme, fixed:

| Element | Before | After |
| --- | --- | --- |
| Pin type labels and the empty-graph hint (`#7E838C` on the node body) | 3.87:1 | 5.22:1 (`#959AA3`) |
| Selected rows in My Project (default text on the accent `#4C8DFF`) | 3.20:1 | 6.60:1 (new `IdeSelection` token, `#2D4F86`) |

The light theme was designed against the same checks and passes them all. The light and system themes are chosen in Tools → Preferences.

Color is never the only cue: execution and data pins have different shapes, data pins are labeled with their type, and errors also show a message in Problems.

## UI scale (done, D-044)

Tools → Preferences → UI scale (75–200 %) scales the whole window. Menus, flyouts, and tooltips take the same scale (`Popup.InheritsTransform`, checked by `UiScaleAppliesToTheWindowAndItsPopups`).

## Remaining

- Keyboard reachability of every command: audit menus, panels, and overlays.
- Visible focus on every focusable control, in both themes.
- An automation peer for the canvas that exposes nodes, pins, and connections to screen readers (Narrator, Orca), with the existing keyboard connecting (Ctrl+L) as the way to edit.
