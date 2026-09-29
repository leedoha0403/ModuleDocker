using System.Text.RegularExpressions;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public enum ValidationSeverity { Warning, Error }

public sealed record ValidationIssue(ValidationSeverity Severity, string Message);

/// <summary>Checks a manifest against the compliance rules of the widget contract.</summary>
public static partial class ManifestValidator
{
    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9]+){2,}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<ValidationIssue> Validate(WidgetManifest m, Version? hostContract = null)
    {
        var host = hostContract ?? ContractInfo.Current;
        var issues = new List<ValidationIssue>();
        void Error(string msg) => issues.Add(new(ValidationSeverity.Error, msg));
        void Warn(string msg) => issues.Add(new(ValidationSeverity.Warning, msg));

        if (string.IsNullOrWhiteSpace(m.Id)) Error("Id is required.");
        else if (!IdPattern().IsMatch(m.Id))
            Warn($"Id '{m.Id}' should be lowercase reverse-domain form '<domain>.<product>.<widget>'.");

        if (string.IsNullOrWhiteSpace(m.Name)) Error("Name is required.");

        if (m.ContractVersion.Major != host.Major)
            Error($"Contract major version {m.ContractVersion.Major} is incompatible with Host {host.Major}.");
        else if (m.ContractVersion > host)
            Warn($"Widget expects contract {m.ContractVersion}, newer than Host {host}.");

        if (m.Layout is null) { Error("Layout is required."); return issues; }

        foreach (var mode in Enum.GetValues<WidgetDisplayMode>())
        {
            var pref = m.Layout.PreferredSize(mode);
            var min = m.Layout.MinimumSize(mode);
            if (pref.Width <= 0 || pref.Height <= 0) Error($"{mode} size must be positive.");
            if (min.Width <= 0 || min.Height <= 0) Error($"Min{mode} size must be positive.");
            if (min.Width > pref.Width || min.Height > pref.Height)
                Error($"Min{mode} size must not exceed {mode} size.");
        }

        if (m.Layout.CollapsedSize.Height > m.Layout.CompactSize.Height ||
            m.Layout.CompactSize.Height > m.Layout.NaturalSize.Height)
            Warn("Expected Collapsed <= Compact <= Natural heights.");

        return issues;
    }

    public static bool IsValid(IReadOnlyList<ValidationIssue> issues) =>
        issues.All(i => i.Severity != ValidationSeverity.Error);
}
