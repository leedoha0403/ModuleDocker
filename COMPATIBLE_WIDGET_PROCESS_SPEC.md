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
