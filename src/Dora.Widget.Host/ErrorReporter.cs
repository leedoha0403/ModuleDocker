namespace Dora.Widget.Host;

/// <summary>
/// Central place for unhandled exceptions. Everything is logged; the user is only interrupted with a dialog
/// when an error is new, so a widget timer that throws every second cannot bury the Host in message boxes.
/// </summary>
public sealed class ErrorReporter
{
    private readonly Action<string> _log;
    private readonly Action<string>? _notifyUser;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _quiet;
    private readonly Dictionary<string, (DateTime LastShown, int Suppressed)> _seen = new();

    /// <param name="notifyUser">Shows a dialog; null means log only.</param>
    /// <param name="quietPeriod">The same error is not shown again for this long.</param>
    public ErrorReporter(Action<string> log, Action<string>? notifyUser, TimeSpan? quietPeriod = null, Func<DateTime>? now = null)
    {
        _log = log;
        _notifyUser = notifyUser;
        _quiet = quietPeriod ?? TimeSpan.FromMinutes(1);
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Returns true when the user was notified.</summary>
    public bool Report(string source, Exception ex)
    {
        var key = ex.GetType().FullName + "|" + ex.Message;
        var now = _now();

        if (_seen.TryGetValue(key, out var seen) && now - seen.LastShown < _quiet)
        {
            _seen[key] = (seen.LastShown, seen.Suppressed + 1);
            _log($"[Error] {source}: {ex.GetType().Name}: {ex.Message} (repeated, suppressed {seen.Suppressed + 1}x)");
            return false;
        }

        _seen[key] = (now, 0);
        _log($"[Error] {source}: {ex}");
        if (_notifyUser is null) return false;
        try { _notifyUser($"{ex.Message}{Environment.NewLine}{Environment.NewLine}Details are in host.log."); }
        catch (Exception) { /* a failing dialog must not raise another unhandled exception */ }
        return true;
    }
}
