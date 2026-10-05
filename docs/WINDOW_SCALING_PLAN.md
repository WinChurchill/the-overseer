# OVERSEER — scaling our HUD

Status: implemented and merged into reviewed main; this file preserves the original
scaling plan and its revision-specific baselines. Current qualification is in
[RELEASE_READINESS.md](RELEASE_READINESS.md); earlier evidence is historical.
The current eight-action menu/settings contract is in UI_SPEC_v0.2.md and README.md.
The unused count-chart renderer was removed; bounded task/project history remains.
Implementation base: b804274 on ux/first-use.
This supersedes the standard-title-bar direction of the earlier scaling plan.
Completed implementation and evidence remain in IMPLEMENTATION_STATE.md.

## Goal

One recognizable OVERSEER HUD at every size: compact, expanded, freely resized,
snapped or maximized. No stock Windows title bar and no separate-looking Window
view. Windows supplies window management underneath OVERSEER's own presentation.

## Keep the work already completed

- Shared controller, window, monitoring and voice session.
- Tested native resizing/restore bounds and independent saved floating geometry.
- PerMonitorV2; real 125% to 100% monitor transitions passed.
- Adaptive task list, short-window scrolling, light constraints, graph rendering.
- Existing settings migration, privacy rules, focus behavior and voice anti-spam.
- Baseline: 453 passing tests; 28 package smoke checks on the preceding candidate,
  with final candidate scoped checks documented separately.

The current SetWindowView method uses SingleBorderWindow for the larger view.
Replace that presentation decision; do not discard the underlying sizing work.

## User-facing behavior

- The existing OVERSEER header is the only title area. No additional native title
  strip, duplicate branding or second visual style appears when resizing.
- A dedicated expand/collapse control changes content density. A separate custom
  maximize/restore control changes window state. Their glyphs, tooltips and
  accessible names must distinguish these actions.
- Expanded/free-sized HUD supports edge/corner resizing and Windows Snap directly;
  no preliminary switch to "Window view" is required.
- Compact remains a deliberate small summary. Collapsing saves the larger normal
  bounds and restores compact placement; expanding restores those larger bounds.
  Collapsing from maximized/snapped state first leaves that state deliberately.
- Compact supports maximize via the custom control (expands first). Resizing a
  compact HUD enters expanded sizing after a real resize gesture, not a DPI-only
  or programmatic size change. Preserve the previous expanded bounds until that
  gesture is committed.
- Keep the top-right area compact: existing light/mute controls plus an accessible
  menu, expand/collapse and maximize/restore. Minimize and Exit remain available
  through the menu/system menu; avoid crowding in redundant caption buttons.
- Keep the current pin preferences: compact starts with the existing HUD preference,
  larger presentation uses the previously saved larger-view preference (default
  off). Resizing/Snap must not silently change pin state. Expose the active setting
  clearly in the menu.
- More space shows more information rather than uniformly enlarging text.
  Narrow layouts reflow controls/metadata; short layouts scroll. Neither state
  nor menu actions may become inaccessible.
- Light width remains independent and constrained for readability. Adjusting it
  reallocates internal space without resizing/unsnapping a user-sized HUD.

## Pointer and keyboard contract

Priority order for hit testing:
1. A thin outer edge/corner band resizes the whole window, with directional cursor.
2. Explicit controls and menus behave as controls, never window drag regions.
3. The light's interior grip changes light width only; reserve the outermost edge
   for window resizing so the two operations never compete.
4. Empty header and designated upper-summary background move the window.
5. Task rows, scrollbars, links and status actions retain their existing meaning.

- Normal pointer on the upper-right controls and gaps; move cursor only over
  actual draggable background. Maximized windows have no active resize edge.
- Double-click plain header background maximizes/restores; do not intercept
  double-clicks on controls or the clickable task-status action.
- Dragging a maximized window restores/moves through the native window operation.
- Tab/Enter/Space, accessible button names, Alt+Space and system commands remain
  usable. Escape never unexpectedly collapses a snapped/maximized window.
- Background monitoring, voice and settings changes never activate the HUD.
  Explicit user interactions may activate it normally.

## Technical approach and early acceptance gate

Use built-in WPF WindowChrome with our content extending into the title area,
retaining the already opaque HWND (AllowsTransparency=false). Keep the existing
painted background, palettes, icon and shell. Do not add a UI framework or dependency.

Preserve native resize/maximize capabilities even though no stock caption is drawn.
Use a small owned-window interop adapter only for behavior WindowChrome does not
supply. Register/dispose its HwndSource hook with the window lifecycle.

For the custom maximize button, implement appropriate non-client hit testing
(HTMAXBUTTON at its actual screen-space bounds) and mouse/command routing.
A non-client hit changes event routing: explicitly test hover, leave, pressed,
release-inside, release-outside and keyboard invocation; invoke maximize only once.
Do not both handle a WPF click and issue the same native command.

Convert physical cursor coordinates using the live HWND DPI/PointFromScreen.
Support negative monitor coordinates; recompute after layout, DPI and state changes.
Do not use fixed physical-pixel button rectangles.

FIRST prove with our actual header:
- the custom maximize button works by pointer and keyboard;
- hover opens the real Windows 11 Snap layout flyout when enabled;
- Win+Arrow, Win+Z, drag-to-edge and drag-from-maximized work;
- invisible native caption controls do not intercept our header;
- the upper-right menu and light grip never start a window drag.

HTMAXBUTTON alone is not proof of the complete Snap experience. If the prototype
cannot meet this gate, investigate within WPF/Win32 and report the exact obstacle.
Do not silently return to a stock title bar or build a fake Snap flyout.

## Ordered implementation

One implementation goal and one review; these are internal milestones.

1. Inspect current shell, WindowView controller, placement service and input tests.
   Prototype custom chrome against the acceptance gate above before polishing.
2. Define the input map and implement custom header controls, native resize edges,
   system menu and maximize/restore commands. Consolidate existing DragMove logic
   so native caption handling and WPF mouse capture never compete.
3. Reconcile compact/expanded presentation with native normal/snapped/maximized
   states. Keep persisted old WindowView fields readable; migrate their values to
   the corresponding larger HUD state without losing geometry or pin preferences.
   Separate user resize gestures from automatic DPI/layout adjustments.
4. Preserve OS ownership of live bounds. Model, theme, light and task updates must
   not restore a preset size. Preserve normal restore bounds while maximized.
   Use monitor work area on maximize and recovery; do not cover the taskbar.
5. Retune header reflow at the measured minimum width (target <=320 DIPs).
   Keep the existing styles, readable essentials, light behavior and graph limits.
6. Add targeted state/input tests, render all relevant layouts, run native checks,
   build/test/package, review, then record results and qualifications.

## Relevant files

- src/TheOverseer.UI/HudWindow.xaml and HudWindow.xaml.cs
- src/TheOverseer.App/HudApplicationController.cs and .WindowView.cs
- src/TheOverseer.App/Desktop/NativeMethods.cs and WindowPlacementService.cs
- src/TheOverseer.Core/Configuration/HudWindowState.cs and HudPlacement.cs
- Existing theme resources and Diagnostics/HudFixtureRunner.cs
- HudWindowTests, HudDispatchTests, WindowViewStateTests, WindowPlacementTests
- scripts/Test-WindowScaling.ps1 and scripts/Test-PackagedRelease.ps1

Likely one small native-chrome adapter in App/Desktop; no new project.
Leave frozen AGENTS.md/specification, monitoring reducers and voice policy untouched.
Do not overwrite the existing agent handoff during this planning-only revision.

## Completion criteria

- Compact, expanded, snapped and maximized all retain our HUD appearance; no stock
  title strip or invisible clickable caption controls remain.
- Our maximize/restore button and the actual Windows Snap flyout work together.
- Header/controls/light/resize edges have distinct, tested behavior and cursors.
- No input action fires twice, no drag traps/capture leaks, no background focus theft.
- Maximize respects the taskbar; restore recovers expected normal bounds; updates
  and light changes preserve user/OS dimensions.
- Existing state migrates; filters, pin settings, monitoring and voice continuity
  survive collapse/expand/resize/maximize/restore and restart.
- Tests and final package evidence match the actual custom-header implementation.
  Prior native-title-bar tests are not evidence that the new header works.

Validation matrix:
- Light/Dark; compact; narrow/tall; wide/short; half/third/quarter-screen; maximized;
  long labels, large counts, empty/uncertain/attention states; all light positions.
- Policy tests at 100/125/150/175/200/250/300%; physical 125% <-> 100% monitors
  including the left portrait display and negative coordinates.
- Native custom-control hit testing and real Snap interactions, resize/restore,
  restart, taskbar work area, monitor removal and sleep/resume where available.
- Windows accessibility text-size-only support remains a separately disclosed gap;
  no new in-app zoom or unrelated visual redesign in this change.
- A missing environment prevents a particular check, not a claim that it passed.
  Snap hover/keyboard testing is a release gate for this custom-header change.

## References

- [WPF WindowChrome](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome?view=windowsdesktop-10.0)
  separates client-drawn presentation from window-management behavior.
- [Windows Snap layouts](https://learn.microsoft.com/windows/apps/desktop/modernize/apply-snap-layout-menu)
  documents custom maximize-button hit testing.
- [Custom window frames](https://learn.microsoft.com/windows/win32/dwm/customframe)
  explains non-client input handling.

