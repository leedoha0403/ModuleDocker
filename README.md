# ModuleDock

A WPF (.NET 8) widget Host that docks, floats, reorders and auto-hides small widgets.
Specs: `MAIN_DOCKER_HOST.md`, `COMPATIBLE_WIDGET_PROCESS_SPEC.md`.

```bash
dotnet build ModuleDock.sln
dotnet test ModuleDock.sln
src\Dora.Widget.Host\bin\Debug\net8.0-windows\Dora.Widget.Host.exe
```

`.\publish.ps1` produces a runnable folder (`dist\`, Host + bundled widgets; needs the .NET 8 Desktop Runtime).

Stop a running Host before rebuilding (the exe cannot be overwritten while it runs). Run the Host from
`src\Dora.Widget.Host\bin\...`; the copy under `tests\` has no sample widgets.

### End-to-end check with the real mouse

`powershell -File tools\e2e\e2e.ps1` drives the built Debug Host with real mouse input (snap by title-bar drag,
auto-hide, hover reveal, handle drags, widget reorder / detach / re-dock) and prints PASS/FAIL per check.
It moves your physical cursor for about 40 s and aborts if the window under the cursor is not the Host under test.

## Layout

| Project | Role |
|---|---|
| `Dora.Widget.Abstractions` | The widget contract (`IComposableWidget`, manifest, context). No UI dependency. |
| `Dora.Widget.Runtime` | UI-agnostic Host logic: state machines, adaptive layout, dock/drag, host-window snap/auto-hide, persistence, permissions. |
| `Dora.Widget.SDK` | Helpers for widget authors (`WidgetViewModelBase`, `ModeTemplateView`). |
| `Dora.Widget.Host` | The WPF Host (windows, handle strip, menus). |
| `samples/*` | Clock (4-project layout), Counter (minimal), DiskUsage (FileSystem capability, `host.attention`). |

## Writing a module

Follow `Feature.Core / Feature.Presentation / Feature.App / Feature.Widget`. Implement `IComposableWidget`
in `Feature.Widget`, build it, and drop `Feature.Widget.dll` (plus its dependencies) into
`widgets\<Name>\` next to the Host. It is discovered at start-up; the Host code does not change.
Widgets never own docking, focus, pinning, dragging or window placement.

## Host window settings (`%AppData%\ModuleDock\host-settings.json`)

```json
{ "hostWindow": {
    "snapEnabled": true, "snapDistance": 20, "allowedEdges": ["Left", "Right"],
    "autoHideEnabled": false, "autoHideDelayMs": 500, "revealDelayMs": 150, "handleThickness": 10,
    "placementState": "Snapped", "snappedEdge": "Right", "handleOffsetRatio": 0.4 } }
```

Also editable from the gear button in the Host. Distances are DIPs scaled per monitor; snapping uses the
monitor work area (taskbar excluded). Auto-hide never triggers while the pointer is inside the Host, a widget is
being dragged, a menu/dialog is open, the Host is being moved/resized or a drop preview is shown. Pinned widgets
do not block auto-hide. Widgets can light the handle with the event `host.attention` (payload `true`/`false`).

### Widget interaction tuning (same file, `interaction` section)

```json
{ "interaction": { "hoverDelayMs": 200, "focusReleaseDelayMs": 300, "dragThreshold": 6, "detachMargin": 24, "itemSpacing": 4 } }
```

These are Host preferences (also in the gear dialog) and apply immediately; widgets cannot change them.

## Robustness

- One Host per data folder: launching a second time brings the running one forward (and reveals an auto-hidden Host).
- Unhandled exceptions are logged to `host.log`; the same error only opens a dialog once a minute.
- A UI watchdog logs when the UI thread is blocked for more than 5 s (widgets share the Host process, so a widget
  that blocks the UI freezes the Host; the log shows when).
- `host.log` rotates to `host.log.1` at 1 MB; a broken plugin dll is reported in the log and skipped.
- The Host window cannot be made narrower than the widest declared Collapsed minimum of its widgets.

## Other Host data

`layout.json` (dock order, floating bounds, pins), `state\<instance>.json` (widget state),
`permissions.json` (capabilities the user granted per widget), `host.log`.
Pass `--data <dir>` to use another folder.
