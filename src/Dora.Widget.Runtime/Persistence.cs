using System.Text.Json;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

// ---- Widget (feature) state ------------------------------------------------

public sealed record StoredWidgetState(int StateVersion, string Json);

public interface IWidgetStateStore
{
    StoredWidgetState? Load(string instanceId);
    void Save(string instanceId, StoredWidgetState state);
    void Delete(string instanceId);
}

public sealed class InMemoryWidgetStateStore : IWidgetStateStore
{
    private readonly Dictionary<string, StoredWidgetState> _items = new();
    public StoredWidgetState? Load(string instanceId) => _items.GetValueOrDefault(instanceId);
    public void Save(string instanceId, StoredWidgetState state) => _items[instanceId] = state;
    public void Delete(string instanceId) => _items.Remove(instanceId);
}

public sealed class FileWidgetStateStore : IWidgetStateStore
{
    private readonly string _directory;

    public FileWidgetStateStore(string directory) => _directory = directory;

    public StoredWidgetState? Load(string instanceId)
    {
        var path = PathFor(instanceId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<StoredWidgetState>(File.ReadAllText(path)); }
        catch (JsonException) { return null; } // corrupt state must not block startup
    }

    public void Save(string instanceId, StoredWidgetState state) =>
        AtomicFile.WriteAllText(PathFor(instanceId), JsonSerializer.Serialize(state));

    public void Delete(string instanceId)
    {
        var path = PathFor(instanceId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string instanceId)
    {
        // instance ids are host-generated GUIDs; still refuse anything path-like.
        if (instanceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid instance id.", nameof(instanceId));
        return Path.Combine(_directory, instanceId + ".json");
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

internal sealed class StateWriter : IWidgetStateWriter
{
    public StoredWidgetState? Result { get; private set; }
    public void Write(int stateVersion, string json) => Result = new StoredWidgetState(stateVersion, json);
}

internal sealed class StateReader : IWidgetStateReader
{
    private readonly StoredWidgetState? _state;
    public StateReader(StoredWidgetState? state) => _state = state;

    public bool TryRead(out int stateVersion, out string json)
    {
        stateVersion = _state?.StateVersion ?? 0;
        json = _state?.Json ?? "";
        return _state is not null;
    }
}

// ---- Host layout ------------------------------------------------------------

public sealed record LayoutEntry(
    string InstanceId,
    string WidgetId,
    DockState DockState,
    int DockOrder,
    bool Pinned,
    WidgetRect? FloatingBounds);

public sealed record LayoutSnapshot(
    int SchemaVersion,
    WidgetRect? HostBounds,
    IReadOnlyList<LayoutEntry> Entries)
{
    public const int CurrentSchema = 1;
}

public interface ILayoutStore
{
    LayoutSnapshot? Load();
    void Save(LayoutSnapshot snapshot);
}

public sealed class InMemoryLayoutStore : ILayoutStore
{
    private LayoutSnapshot? _snapshot;
    public LayoutSnapshot? Load() => _snapshot;
    public void Save(LayoutSnapshot snapshot) => _snapshot = snapshot;
}

public sealed class JsonLayoutStore : ILayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly string _path;
    public JsonLayoutStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModuleDock", "layout.json");

    public LayoutSnapshot? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<LayoutSnapshot>(File.ReadAllText(_path), Options);
            return s is { SchemaVersion: LayoutSnapshot.CurrentSchema } ? s : null;
        }
        catch (JsonException) { return null; }
    }

    public void Save(LayoutSnapshot snapshot) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Options));
}

/// <summary>Builds and applies layout snapshots (Host-owned state only).</summary>
public sealed class LayoutManager
{
    private readonly ILayoutStore _store;
    public LayoutManager(ILayoutStore store) => _store = store;

    public LayoutSnapshot Capture(IEnumerable<WidgetRuntimeState> states, WidgetRect? hostBounds,
        IEnumerable<LayoutEntry>? preserved = null)
    {
        var entries = states
            .OrderBy(s => s.DockState == DockState.Docked ? 0 : 1)
            .ThenBy(s => s.DockOrder)
            .Select(s => new LayoutEntry(
                s.InstanceId, s.WidgetId, s.DockState, s.DockOrder,
                s.InteractionState == WidgetInteractionState.Pinned ||
                (s.InteractionState == WidgetInteractionState.Dragging &&
                 s.PreviousInteractionState == WidgetInteractionState.Pinned),
                s.FloatingBounds))
            .ToList();
        if (preserved != null) entries.AddRange(preserved);
        return new LayoutSnapshot(LayoutSnapshot.CurrentSchema, hostBounds, entries);
    }

    public void Save(IEnumerable<WidgetRuntimeState> states, WidgetRect? hostBounds,
        IEnumerable<LayoutEntry>? preserved = null) =>
        _store.Save(Capture(states, hostBounds, preserved));

    public LayoutSnapshot? Load() => _store.Load();
}
