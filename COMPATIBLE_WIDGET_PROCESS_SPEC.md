# Compatible Widget / Process Contract

## 1. Purpose

This document defines the mandatory contract that every future application/module must follow if it is intended to be hosted by the common Docker/Widget Host.

A compatible module must support both:

```text
Standalone application usage
AND
Hosted child-widget usage
```

The Host owns docking, dragging, focus transitions, floating windows, layout and common interaction behavior.

The module owns its feature logic, presentation, state and declared capabilities.

---

# 2. Required Project Separation

Every compatible feature should be separated conceptually as follows:

```text
FeatureName.Core
FeatureName.Presentation
FeatureName.App
FeatureName.Widget
```

### FeatureName.Core

Contains:

- business logic
- data acquisition
- models
- service interfaces
- provider logic

Must not depend on the Docker Host.

### FeatureName.Presentation

Contains:

- ViewModels
- common presentation state
- summary/detail presentation logic
- display mode aware UI

### FeatureName.App

Contains the standalone application shell/window.

### FeatureName.Widget

Contains the Host adapter and implements the common widget contract.

---

# 3. Mandatory Manifest Fields

Every compatible widget MUST provide all required fields below.

```csharp
public sealed record WidgetManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Version Version { get; init; }
    public required Version ContractVersion { get; init; }

    public required WidgetLayoutProfile Layout { get; init; }
    public required WidgetCapabilities Capabilities { get; init; }

    public required bool AllowMultipleInstances { get; init; }
    public required bool SupportsDetailView { get; init; }
    public required bool SupportsFloating { get; init; }

    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? IconKey { get; init; }
    public string? Category { get; init; }
}
```

## Mandatory field requirements

### Id

Required.

Must be globally unique and immutable after release.

Recommended format:

```text
<reverse-domain>.<product>.<widget>
```

Example:

```text
dev.leedoha.aiusage.summary
dev.leedoha.diskusage.overview
dev.leedoha.agentdesk.queue
```

Do NOT use the display name as the ID.

### Name

Required.

Human-readable display name.

### Version

Required.

Version of the widget implementation.

### ContractVersion

Required.

Version of the common Host contract expected by the widget.

The Host must reject or warn about incompatible major contract versions.

### Layout

Required.

Must declare size requirements for each display mode.

### Capabilities

Required.

Must declare external privileges/services needed by the widget.

### AllowMultipleInstances

Required.

Specifies whether more than one instance of the same Widget ID can be created.

### SupportsDetailView

Required.

Must be true only when CreateDetailView is implemented.

### SupportsFloating

Required.

Specifies whether this widget can be detached into a floating mini-window.

---

# 4. Mandatory Layout Profile

Every widget MUST declare the following values.

```csharp
public sealed record WidgetLayoutProfile
{
    public required Size NaturalSize { get; init; }
    public required Size CompactSize { get; init; }
    public required Size CollapsedSize { get; init; }

    public required Size MinNaturalSize { get; init; }
    public required Size MinCompactSize { get; init; }
    public required Size MinCollapsedSize { get; init; }
}
```

## Meaning

### NaturalSize

The preferred size of the original mini-docker UI.

This is the size used when:

- hovered/focused
- pinned
- floating

unless constrained by Host policy.

### CompactSize

Preferred size for normal docked summary mode.

Must still expose the most important live state.

Example:

```text
Claude 72% / reset 02:14
```

### CollapsedSize

Minimum summary representation.

Should normally show only:

- icon
- short name
- optionally one critical value/status

Example:

```text
[AI] Claude
```

---

# 5. Mandatory Widget Interface

Every compatible module MUST implement a common interface equivalent to:

```csharp
public interface IComposableWidget
{
    WidgetManifest Manifest { get; }

    Task InitializeAsync(
        IWidgetContext context,
        CancellationToken cancellationToken);

    object CreateSummaryView(IWidgetContext context);

    object? CreateDetailView(IWidgetContext context);

    Task SaveStateAsync(IWidgetStateWriter writer);

    Task RestoreStateAsync(IWidgetStateReader reader);

    Task ShutdownAsync(CancellationToken cancellationToken);
}
```

---

# 6. Mandatory Summary View Behavior

CreateSummaryView MUST return UI that supports all three Host-controlled display modes:

```text
Natural
Compact
Collapsed
```

The widget MUST NOT determine when those modes are selected.

The Host selects the mode.

The widget is responsible only for rendering correctly for the selected mode.

Recommended implementation:

```text
One shared ViewModel
    +
NaturalTemplate
CompactTemplate
CollapsedTemplate
```

or an equivalent responsive view.

---

# 7. Detail View Rules

If:

```text
SupportsDetailView = true
```

then CreateDetailView MUST return a valid detail view.

The detail view should be used for:

- graphs
- history
- configuration
- advanced controls
- logs
- full content

The detail view MUST NOT assume it owns the application MainWindow.

The Host decides how the detail view is wrapped in a Window.

A widget whose detail view needs more room than the Host default (640x480) declares it in the manifest
(optional `PreferredDetailSize` and `MinDetailSize`, DIP). The Host uses them for the detail window it creates,
clamped to the screen work area. The widget still never creates or sizes the window itself.

---

# 8. Floating Rules

If:

```text
SupportsFloating = true
```

then the same Summary View must be usable in a floating container.

Do not implement a separate docking engine inside the widget.

Preferred model:

```text
SummaryView
   -> DockContainer
```

or

```text
SummaryView
   -> FloatingWindow
```

The Host owns both containers.

---

# 9. Mandatory Shared State Rule

The widget must not create unrelated state for:

```text
Docked view
Floating view
Detail view
```

All presentation surfaces should share the same feature state/ViewModel where possible.

Recommended structure:

```text
FeatureViewModel
├─ SummaryView
├─ Floating SummaryView
└─ DetailView
```

A change in one surface should be reflected in the others.

---

# 10. Instance Identity

The Host assigns a unique InstanceId to every instance.

Widget type ID and instance ID are separate concepts.

Example:

```text
WidgetId:
dev.leedoha.diskusage.overview

InstanceId:
4dfdc280-85ab-42e3-8af2-...
```

The widget must never use WidgetId as an instance key when AllowMultipleInstances is true.

---

# 11. State Persistence

Every compatible widget MUST implement state save/restore.

The widget persists only feature-specific state.

Examples:

```text
selected provider
selected disk path
sort order
filter
view options
selected repository
```

The widget MUST NOT persist Host-owned state such as:

```text
dock order
floating bounds
dock/floating state
Host position
Host dimensions
```

Those belong to the Host.

---

# 12. State Versioning

Persistent state SHOULD include a state schema version.

Example:

```json
{
  "stateVersion": 2,
  "state": {
    "provider": "Claude"
  }
}
```

If the state schema changes, the module is responsible for migrating its own feature state.

---

# 13. Mandatory Host Context Usage

Widgets MUST access Host functionality through IWidgetContext or equivalent abstractions.

Example:

```csharp
public interface IWidgetContext
{
    string InstanceId { get; }

    IWidgetEventBus Events { get; }
    IWidgetCommandBus Commands { get; }
    IWidgetSettings Settings { get; }
    IWidgetNotificationService Notifications { get; }
    IWidgetPermissionService Permissions { get; }
    ILogger Logger { get; }
}
```

Widgets MUST NOT reach into Host MainWindow or DockManager internals directly.

---

# 14. Events and Commands

Widgets must communicate through shared contracts rather than directly referencing other widget implementations.

Event example:

```text
filesystem.path.selected
agent.execution.completed
aiusage.provider.changed
```

Command example:

```text
open.repository
refresh.usage
open.path
```

Recommended naming:

```text
<domain>.<resource>.<action>
```

---

# 15. Capabilities

Every widget MUST declare required capabilities.

Suggested values:

```csharp
[Flags]
public enum WidgetCapabilities
{
    None = 0,
    FileSystem = 1 << 0,
    Network = 1 << 1,
    ProcessExecution = 1 << 2,
    Clipboard = 1 << 3,
    Notifications = 1 << 4,
    Git = 1 << 5,
    Shell = 1 << 6
}
```

Widgets must not silently use undeclared capabilities.

---

# 16. Permissions

Capabilities describe what a module may need.

Permissions describe what the Host/user has actually granted.

Widgets must request sensitive actions through Host APIs.

Do not bypass Host permission checks.

---

# 17. Interaction Ownership

The following MUST be controlled by the Host, not by the widget:

- Hover delay
- Focus state
- Pin state
- Drag start threshold
- Drag reorder
- Dock/undock
- Floating window creation
- Detail window shell creation
- Focus release
- Adaptive layout allocation

The widget only reacts to display mode and feature actions.

---

# 18. Required Display Mode Contract

Each widget must correctly support:

```csharp
public enum WidgetDisplayMode
{
    Collapsed,
    Compact,
    Natural
}
```

Minimum behavior requirements:

### Collapsed

MUST show:

- recognizable icon or name

SHOULD show:

- one critical status/value if practical

### Compact

MUST show:

- name/identity
- primary status/value

SHOULD show:

- secondary status if space permits

### Natural

MUST show:

- the original intended mini-docker content
- all primary controls intended for the mini view

---

# 19. Standalone Compatibility

The same feature should remain runnable as a standalone app.

Preferred pattern:

```text
Feature.Core
       ↑
Feature.Presentation
       ↑        ↑
Feature.App    Feature.Widget
```

The standalone shell and Host widget adapter reuse the same feature implementation.

---

# 20. Forbidden Patterns

Compatible modules MUST NOT:

- implement their own independent docking framework
- directly mutate Host layout collections
- directly control another widget instance
- directly access another widget's UI object
- assume a fixed Host size
- assume they are always Docked
- assume they are always Floating
- assume a detail window always exists
- use display name as persistent identity
- rely on an external process HWND being forcibly parented into the Host

---

# 21. Minimum Compliance Checklist

A module is considered Host-compatible only if all items below are satisfied.

```text
[ ] Unique immutable WidgetManifest.Id
[ ] Name defined
[ ] Widget Version defined
[ ] ContractVersion defined
[ ] NaturalSize defined
[ ] CompactSize defined
[ ] CollapsedSize defined
[ ] Minimum sizes defined
[ ] Capabilities declared
[ ] AllowMultipleInstances explicitly set
[ ] SupportsDetailView explicitly set
[ ] SupportsFloating explicitly set
[ ] Implements InitializeAsync
[ ] Implements CreateSummaryView
[ ] Implements state Save/Restore
[ ] Implements ShutdownAsync
[ ] Correctly renders Natural mode
[ ] Correctly renders Compact mode
[ ] Correctly renders Collapsed mode
[ ] Uses Host Context for cross-system services
[ ] Does not own docking behavior
[ ] Shares feature state between docked/floating/detail surfaces
```

If SupportsDetailView is true:

```text
[ ] CreateDetailView implemented
```

If SupportsFloating is true:

```text
[ ] Summary View works inside Host-provided floating container
```

---

# 22. Recommended Naming

Suggested shared packages:

```text
Dora.Widget.Abstractions
Dora.Widget.SDK
Dora.Widget.Runtime
Dora.Widget.Host
```

Example feature:

```text
AIUsage.Core
AIUsage.Presentation
AIUsage.App
AIUsage.Widget
```

This keeps future applications compatible with the same Host without coupling their business logic to the Host implementation.

---

# 23. Design and Theme

Every compatible module must inherit the Host theme and follow the shared visual rules (fonts, colors, radius,
spacing) so that hosted widgets look like one family. The keys, values and checklist are defined in
`WIDGET_DESIGN_GUIDE.md`; its checklist is part of the compatibility checklist of this document.

The Host offers three theme modes, chosen by the user in the Host settings: **System (recommended, default)**,
**Dark** and **Light**. System follows the Windows app mode and changes live. Theme is Host-owned: a module never
selects a theme itself. On a theme change the Host replaces the `ModuleDock.*` resources with new frozen
objects, so a module must reference them with `DynamicResource` (values read once with `FindResource`, or
`StaticResource`, go stale; a module that copies them into its own keys re-copies when `ModuleDock.Theme.Token`
changes), and must stay readable in both palettes.

```text
[ ] Colors, fonts, radius and padding come from ModuleDock.* theme resources (no hard-coded values)
[ ] Falls back to identical defaults when the Host theme is absent (standalone use)
[ ] Readable in both Dark and Light themes and follows a live theme change without being recreated
```

---

# 24. Summary Content Density (Compact / Normal / Detailed)

A mini widget that has a user-selectable amount of detail (for example "간단히 / 중간 / 자세히") MUST expose it
as a **feature option** that every surface honors. This is separate from `WidgetDisplayMode`:

```text
WidgetDisplayMode  (Collapsed / Compact / Natural)  -> chosen by the Host, decides HOW MUCH SPACE the widget gets
SummaryDensity     (Compact / Normal / Detailed)    -> chosen by the user, decides HOW MUCH CONTENT Natural shows
```

Rules:

- The density option belongs to the widget's own feature state, not to the Host. It is persisted through
  `SaveStateAsync` / `RestoreStateAsync` (section 11) and versioned with the rest of the feature state.
- The same option drives the standalone window and the Host-hosted Natural view. Moving a widget between the
  standalone app and the Host MUST NOT change what it shows.
- The option lives on the shared feature ViewModel (section 9), so a change is reflected in the docked, floating
  and detail surfaces at once.
- It is changed from the widget's own settings (Detail View), never by the Host. The Host does not know about it.
- Recommended values and meaning:

```text
Compact   only the primary value per item (name + main percentage / status)
Normal    primary value plus the most useful secondary line (default)
Detailed  everything the original mini widget could show
```

- `Collapsed` and `Compact` display modes ignore the density option; they always render their fixed minimum content.
- `NaturalSize` should be declared for the content Natural shows at the default density; when the user picks a
  denser option the Host may fall back to a smaller display mode or scroll (MAIN_DOCKER_HOST.md), so content must
  never be clipped silently without a scroll or downgrade.

```text
[ ] The density option lives in the feature ViewModel and feature state (not in the Host)
[ ] Standalone window and Host Natural view render the same content for the same density
[ ] Density is editable from the widget's settings and survives Save/Restore
```

---

# 25. Instance and Owner Cardinality (MUST be declared)

Every compatible module MUST state explicitly whether it may exist more than once. "Not stated" is not allowed.
Two questions must both be answered:

```text
1. Inside the Host:   may the Host hold more than one instance of this widget?   -> WidgetManifest.AllowMultipleInstances
2. Across homes:      may the widget's surface be shown by the standalone app and by a Host at the same time,
                      or by several standalone processes at the same time?      -> the module's Owner Policy
```

## Owner policy values

```text
SingleOwner       The module's surface (its mini widget) has exactly one live owner in the whole system. The
                  standalone app and the Host widget are two homes of the same single surface: while one shows it
                  the other MUST NOT. A start that would create a second owner is refused (or forwarded to the
                  existing owner), never run in parallel. A helper process kept for the hand-over (for example the
                  app staying in the tray after its widget was docked) is allowed as long as it does not show the
                  surface.
MultiOwner        Several owners may exist at once (standalone + Host, or several standalone processes).
                  The module must then keep its own data safe under concurrent owners.
```

Rules:

- `SingleOwner` implies `AllowMultipleInstances = false`. `AllowMultipleInstances = true` with `SingleOwner`
  is invalid.
- A `SingleOwner` module enforces this itself with system-wide guards (for example named mutexes) that both the
  standalone app and the widget use. The Host only refuses a second instance inside itself; it cannot see other
  processes.
- The widget refuses to initialize when another owner already shows the surface, except while that owner is handing
  it over (drop of the app's mini widget onto the Host). The standalone app refuses a plain start while a Host
  holds the widget; only a Host-started hand-over start may run next to it.
- A widget declines by throwing `WidgetRefusedException` from `InitializeAsync`. The Host logs it and skips the
  widget (restore, "+" menu, drop) without an error dialog; any other exception is still reported as an error.
- Moving between homes (drag out of the Host / drop into the Host) is a hand-over of the one owner
  (the Host owns docking, section 17), never a copy: the old owner releases before the new one shows the surface.
- The policy is part of the module's contract: document it in the widget manifest description or README and keep it
  stable across releases (changing it is a compatibility change).

```text
[ ] AllowMultipleInstances is explicitly set
[ ] Owner policy (SingleOwner / MultiOwner) is explicitly declared
[ ] SingleOwner modules guard system-wide across standalone app and Host widget
[ ] A start that would create a second owner is refused or forwarded, never run in parallel
[ ] Hand-over releases the old owner before the new owner shows the surface
```

## Declared policies of existing modules

```text
AI Usage (dev.leedoha.aiusage.summary)   SingleOwner   AllowMultipleInstances = false
```

---

# 26. Host Packaging, Release and Self-Update

The Host is released as a **self-contained single-file exe** (`ModuleDock.exe`) following
`SELF_UPDATE_RELEASE_GUIDE.md`. This is Host-level only: a compatible module needs no code for it, but must respect
the layout below so that an update never breaks it.

```text
ModuleDock.exe            single file, replaced in place by the auto-update (name is fixed)
widgets\<Name>\*.dll      module plugins, read from next to the exe; never touched by the exe replacement
%AppData%\ModuleDock\     Host settings, layout, widget state, permissions, host.log (survive updates)
```

Rules for modules:

```text
[ ] Read and write only through IWidgetContext / the widget state store; never next to ModuleDock.exe
[ ] Do not reference the Host's exe or assembly name (it is ModuleDock, not Dora.Widget.Host)
[ ] Ship as widgets\<Name>\ (the Widget project plus its Core/Presentation DLLs); do NOT copy Dora.Widget.*.dll
    (the widget must share the Host's copy of the contract types, which live inside the single-file exe)
```

Release flow (tag `vX.Y.Z` -> `.github/workflows/release.yml` -> `tools/publish-release.ps1`):

| Asset | Purpose |
|---|---|
| `ModuleDock.exe` | in-place auto-update (fixed name, hash required in `SHA256SUMS.txt`) |
| `ModuleDock-vX.Y.Z-win-x64.zip` | first install and manual update: exe plus the bundled sample `widgets\` |
| `SHA256SUMS.txt` | hashes of the two files above |

The auto-update replaces **only the exe**. Widgets bundled in the zip (Clock, Counter, DiskUsage) are updated by
unpacking the zip over the install folder; third-party modules such as AI Usage are deployed into `widgets\`
by their own tooling. The Host checks GitHub on start (silent on failure), shows a header button when a newer
version exists, and never installs without the user's confirmation. Version is set in
`src/Dora.Widget.Host/Dora.Widget.Host.csproj` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`).