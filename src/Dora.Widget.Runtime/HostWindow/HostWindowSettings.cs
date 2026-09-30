using System.Text.Json;
using System.Text.Json.Serialization;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime.HostWindow;

/// <summary>
/// Host window preferences plus the last placement. Host-owned: widgets never see any of this.
/// Distances (<see cref="SnapDistance"/>, <see cref="HandleThickness"/>) are DIPs and are scaled by
/// the DPI of the monitor the Host is currently on.
/// </summary>
public sealed record HostWindowSettings
{
    public bool SnapEnabled { get; init; } = true;
    public double SnapDistance { get; init; } = 20;
    public SnapEdges AllowedEdges { get; init; } = SnapEdges.Left | SnapEdges.Right;

    public bool AutoHideEnabled { get; init; }
    public int AutoHideDelayMs { get; init; } = 500;
    public int RevealDelayMs { get; init; } = 150;
    public double HandleThickness { get; init; } = 16;

    /// <summary>Keep the Host above other windows even while floating (a snapped Host is always on top).</summary>
    public bool AlwaysOnTop { get; init; }

    // ---- last placement ----
    public HostPlacementState PlacementState { get; init; } = HostPlacementState.Floating;
    public ScreenEdge SnappedEdge { get; init; } = ScreenEdge.None;
    public double HandleOffsetRatio { get; init; } = 0.4;
    public string? MonitorId { get; init; }

    /// <summary>Host window size in DIPs, used to size it when it is restored snapped or on another monitor.</summary>
    public WidgetSize? HostSizeDip { get; init; }

    /// <summary>Floating bounds in physical pixels; <see cref="FloatingDpiScale"/> is the DPI they were saved at.</summary>
    public WidgetRect? FloatingBounds { get; init; }
    public double FloatingDpiScale { get; init; } = 1.0;

    private static bool IsUsable(WidgetRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height) &&
        r.Width >= 80 && r.Height >= 60 && r.Width < 20000 && r.Height < 20000;

    /// <summary>Returns a copy with every value forced into its valid range.</summary>
    public HostWindowSettings Sanitized()
    {
        var snapped = PlacementState == HostPlacementState.Snapped &&
                      SnappedEdge != ScreenEdge.None &&
                      (AllowedEdges & SnappedEdge.ToFlag()) != 0 && SnapEnabled;
        return this with
        {
            SnapDistance = Math.Clamp(double.IsNaN(SnapDistance) ? 20 : SnapDistance, 0, 200),
            AutoHideDelayMs = Math.Clamp(AutoHideDelayMs, 0, 60_000),
            RevealDelayMs = Math.Clamp(RevealDelayMs, 0, 10_000),
            HandleThickness = Math.Clamp(double.IsNaN(HandleThickness) ? 16 : HandleThickness, 4, 64),
            HandleOffsetRatio = Math.Clamp(double.IsNaN(HandleOffsetRatio) ? 0.4 : HandleOffsetRatio, 0, 1),
            FloatingDpiScale = FloatingDpiScale > 0 ? FloatingDpiScale : 1.0,
            // A missing or zero-sized rectangle (bad hand edit, wrong keys) must not produce a 1 px window.
            FloatingBounds = FloatingBounds is { } fb && IsUsable(fb) ? fb : null,
            HostSizeDip = HostSizeDip is { } hs && hs.Width >= 100 && hs.Height >= 60 && hs.Width < 10000 && hs.Height < 10000 ? hs : null,
            PlacementState = snapped ? HostPlacementState.Snapped : HostPlacementState.Floating,
            SnappedEdge = snapped ? SnappedEdge : ScreenEdge.None
        };
    }
}

/// <summary>
/// Reads/writes host-settings.json: <c>{ "hostWindow": { ... }, "interaction": { ... } }</c>
/// (camelCase, enums as strings). Saving one section keeps the other untouched.
/// </summary>
public sealed class HostSettingsStore
{
    private sealed record Root(HostWindowSettings? HostWindow, HostInteractionSettings? Interaction);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // the file is meant to be hand-editable
        // Order matters: the first converter that can handle a type wins, and SnapEdges must not fall to the generic enum one.
        Converters = { new SnapEdgesConverter(), new JsonStringEnumConverter() }
    };

    private readonly string _path;
    public HostSettingsStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModuleDock", "host-settings.json");

    /// <summary>Missing or corrupt files yield defaults; a settings problem must never block startup.</summary>
    public HostWindowSettings Load() => (ReadRoot().HostWindow ?? new HostWindowSettings()).Sanitized();

    public HostInteractionSettings LoadInteraction() => (ReadRoot().Interaction ?? new HostInteractionSettings()).Sanitized();

    public void Save(HostWindowSettings settings) => Write(ReadRoot() with { HostWindow = settings });

    public void SaveInteraction(HostInteractionSettings settings) => Write(ReadRoot() with { Interaction = settings });

    private Root ReadRoot()
    {
        try
        {
            if (!File.Exists(_path)) return new Root(null, null);
            return JsonSerializer.Deserialize<Root>(File.ReadAllText(_path), Options) ?? new Root(null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Root(null, null);
        }
    }

    private void Write(Root root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(root, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Writes <see cref="SnapEdges"/> as ["Left","Right"]; also accepts a single string.</summary>
internal sealed class SnapEdgesConverter : JsonConverter<SnapEdges>
{
    public override SnapEdges Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = SnapEdges.None;
        if (reader.TokenType == JsonTokenType.String)
            return Parse(reader.GetString());
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of edges.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Edge names must be strings.");
            result |= Parse(reader.GetString());
        }
        return result;
    }

    private static SnapEdges Parse(string? s) =>
        Enum.TryParse<SnapEdges>(s, ignoreCase: true, out var e) ? e : throw new JsonException($"Unknown edge '{s}'.");

    public override void Write(Utf8JsonWriter writer, SnapEdges value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var e in new[] { SnapEdges.Left, SnapEdges.Right, SnapEdges.Top, SnapEdges.Bottom })
            if ((value & e) != 0) writer.WriteStringValue(e.ToString());
        writer.WriteEndArray();
    }
}
