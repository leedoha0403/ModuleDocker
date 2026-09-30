using System.Text.Json;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

/// <summary>Remembers which capabilities the user has granted to which widget type.</summary>
public interface IPermissionGrantStore
{
    WidgetCapabilities Get(string widgetId);
    void Set(string widgetId, WidgetCapabilities granted);
}

public sealed class InMemoryPermissionGrantStore : IPermissionGrantStore
{
    private readonly Dictionary<string, WidgetCapabilities> _grants = new();
    public WidgetCapabilities Get(string widgetId) => _grants.GetValueOrDefault(widgetId);
    public void Set(string widgetId, WidgetCapabilities granted) => _grants[widgetId] = granted;
}

/// <summary>Persists grants as <c>{ "grants": { "widget.id": ["FileSystem", "Network"] } }</c>.</summary>
public sealed class JsonPermissionGrantStore : IPermissionGrantStore
{
    private sealed record Root(Dictionary<string, List<string>>? Grants);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private Dictionary<string, WidgetCapabilities>? _cache;

    public JsonPermissionGrantStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModuleDock", "permissions.json");

    public WidgetCapabilities Get(string widgetId) => Load().GetValueOrDefault(widgetId);

    public void Set(string widgetId, WidgetCapabilities granted)
    {
        var all = Load();
        all[widgetId] = granted;
        var dto = new Root(all.ToDictionary(
            kv => kv.Key,
            kv => Enum.GetValues<WidgetCapabilities>()
                .Where(f => f != WidgetCapabilities.None && (kv.Value & f) == f)
                .Select(f => f.ToString()).ToList()));
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(dto, Options));
    }

    private Dictionary<string, WidgetCapabilities> Load()
    {
        if (_cache != null) return _cache;
        var result = new Dictionary<string, WidgetCapabilities>();
        try
        {
            if (File.Exists(_path))
            {
                var root = JsonSerializer.Deserialize<Root>(File.ReadAllText(_path), Options);
                foreach (var (id, names) in root?.Grants ?? new())
                {
                    var caps = WidgetCapabilities.None;
                    foreach (var n in names)
                        if (Enum.TryParse<WidgetCapabilities>(n, out var c)) caps |= c;
                    result[id] = caps;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            result.Clear(); // an unreadable grants file means nothing is granted
        }
        return _cache = result;
    }
}
