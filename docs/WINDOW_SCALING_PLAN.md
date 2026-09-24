# OVERSEER window scaling — draft for discussion

Status: user approved implementation; see IMPLEMENTATION_STATE.md for verification.
Inspected base: `f3c9499`, branch `ux/first-use`.
This is a proposed post-V1 UX extension, not a rewrite of the frozen four plans.

## Goal

OVERSEER remains readable and controllable as a small floating HUD or a resized,
snapped side window, across Windows display scales and monitor changes. Resizing
changes available content space; it must not simply magnify the whole interface.

## Observed foundation and gaps

- App manifest already declares PerMonitorV2. Placement service and tests cover
  mixed DPI, monitor-relative positions, work-area clamping and removed displays.
- HudWindow uses WindowStyle=None, AllowsTransparency=True, ResizeMode=NoResize.
  Summary row is fixed at 72 DIPs; several labels and chart dimensions are fixed.
- HudApplicationController.ApplyGeometryCore reapplies preset width/height on
  placement, DPI and several settings changes. Enabling resizing alone would
  let this path undo the user's chosen bounds.
- HudWindowState stores monitor/position/DPI/expansion, but no user-sized bounds.
- Activity chart currently stretches a 330x30 drawing. Larger windows need
  geometry calculated for actual content width, without distorted stroke widths.
- Existing automated geometry tests are a foundation, not proof of native Snap
  or real mixed-monitor operation.

## Proposed user behavior

| Situation | Proposed behavior |
| --- | --- |
| Floating HUD | Keep current compact/expanded use, always-on-top preference and quiet non-activating updates. |
| Resizable window | Explicit Window view gives resize edges and standard minimize/maximize/restore behavior; starts expanded. |
| Side-screen use | Window view supports native Windows Snap and keyboard placement. Fill the assigned area; never snap back to HUD presets. |
| Return to HUD | Restore previous floating bounds and expansion state. Preserve filters, selected task, monitoring session and voice state. |
| More height | Show more task rows; keep summary and controls reachable. Scroll the task list. |
| More width | Give task titles/details more room; keep status typography restrained. Do not add unrelated dashboard panels. |
| Narrow/short space | Reflow controls and secondary metadata before clipping; preserve status and action access. Use a measured minimum width, targeting 320 DIPs or less for Window view. |
| Light strip | Keep its independent size setting. In Window view, resizing/toggling the light redistributes internal space instead of resizing or unsnapping the outer window. |
| Display scale change | Follow Windows DPI with one conversion between logical and physical sizes; no extra automatic app zoom. |

Recommended defaults: floating HUD retains its current topmost preference;
Window view starts non-topmost, with an independent remembered pin preference.
Switching view is explicit. Double-clicking a suitable Window-view title area
maximizes/restores; menus, buttons and resize grips never initiate window dragging.
The user approved this two-view behavior and these pin defaults before coding.

## Implementation sequence after agreement

One cohesive implementation goal and one review, with internal milestones:

1. Establish native window behavior first. Prototype WPF WindowChrome/standard
   non-client behavior for resizable Window view, preserving the existing visual
   identity. Check whether changing from the transparent HUD requires recreating
   only the window host; keep the controller, view model and notification session
   alive. No extra process or framework. Verify Win+Arrow, drag-to-edge, maximize,
   restore and Windows 11 Snap menu before committing to custom caption controls.
2. Separate automatic HUD sizing from user/OS-owned Window-view bounds. Define
   sizing ownership for every path: DPI, expansion, light, theme, task updates,
   display topology, startup and reset. Never overwrite native snapped bounds on
   ordinary model updates. Respect taskbar work areas on maximize.
3. Adapt the shared content layout. Derive breakpoints from measured content
   requirements, not physical resolution. Keep one Light/Dark layout; reflow
   rather than shrink essential text. Bound graph height and redraw at actual
   width. Keep menus/settings reachable with keyboard and pointer at every size.
4. Persist view mode, normal restore bounds in DIPs, owning monitor and pin
   preferences; migrate existing state without losing settings. Do not store
   maximized bounds as normal restore bounds. Let Windows own live Snap placement;
   do not promise restoration of an entire Windows Snap group after restart.
5. Harden mixed-DPI and disconnected-monitor transitions, including negative
   screen coordinates. Clamp only when needed for reachability. Do not fight the
   user's ongoing move/resize gesture or repeatedly apply stale DPI geometry.
6. Build, test, package and review the complete change. Update changelog and
   implementation ledger with tested configurations and remaining limitations.

## Code map

- UI: `src/TheOverseer.UI/HudWindow.xaml`, `.xaml.cs`, theme resources and
  `Controls/ActivitySparkline` rendering.
- Orchestration: `src/TheOverseer.App/HudApplicationController.cs`.
- Native placement: `src/TheOverseer.App/Desktop/WindowPlacementService.cs`,
  `NativeMethods.cs`, `app.manifest`.
- State/policy: `src/TheOverseer.Core/Configuration/HudWindowState.cs`,
  `HudPlacement.cs`, state persistence/migration.
- Tests: `WindowPlacementTests`, `HudSettingsTests`, `HudDispatchTests`,
  `HudWindowTests`, `ActivitySparklineTests`, packaged fixture/smoke scripts.
- Preserve monitoring reducers, task meaning, voice dedupe/cooldown and frozen
  specifications. No agent-provider integration or project grouping in this work.

## Acceptance and validation

- Automated state tests: resize survives updates; independent restore bounds;
  state migration; invalid bounds recovery; view switching does not restart voice
  lifecycle; light size cannot unsnap the window; no resize/DPI feedback loops.
- Render matrix: Light/Dark; compact, narrow/tall, half-screen, wide/short and
  maximized; long task names, large counts, empty, attention and unknown states;
  light off and on every edge, including its maximum size.
- Windows scale matrix: 100%, 125%, 150%, 175%, 200%, with 250–300% stress cases.
  Use representative laptop, 1440p, 4K and portrait work areas, not every possible
  Cartesian combination. Separate larger Windows text-size behavior from DPI;
  characterize WPF support and handle it deliberately rather than claim it works.
- Real native checks: Win+Left/Right, Win+Z where available, Snap menu hover,
  drag-to-edge, maximize/restore, monitor-to-monitor moves at different scales,
  unplug/reconnect, sleep/resume and restart. Record actual available hardware;
  simulated placement tests cannot substitute for real mixed-DPI checks.
- Pass: readable essentials, reachable controls, correct pointer/resize cursors,
  no clipped actions/overlap, no unexpected size resets/jumps, preserved filters,
  no background focus theft, no extra notifications caused by view changes.
- If a layout cannot fit physically, provide scrolling/reachable recovery rather
  than silently reducing text below its readable baseline.

## Decisions deferred from this change

Independent in-app zoom can be added after DPI, text-size and reflow behavior are
verified. No custom Snap manager, reserved desktop docking/AppBar, multi-column
dashboard, multiple simultaneous OVERSEER windows, or new agent integrations.

## Platform references

- Microsoft: [Snap layouts for desktop apps](https://learn.microsoft.com/windows/apps/desktop/modernize/apply-snap-layout-menu).
  Custom caption controls must preserve native maximize hit testing; a resizable
  border alone does not establish Snap menu support.
- Microsoft: [WM_DPICHANGED](https://learn.microsoft.com/windows/win32/hidpi/wm-dpichanged).
  Monitor transitions include an updated DPI and suggested window rectangle;
  verify WPF's handling before adding overlapping manual compensation.
