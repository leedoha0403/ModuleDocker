using System.IO;
using System.Windows;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log("Unhandled: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "ModuleDock", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        var dataDir = DataDirectory(args);
        Directory.CreateDirectory(dataDir);
        var logFile = Path.Combine(dataDir, "host.log");
        void WriteLog(string line)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:s} {line}{Environment.NewLine}"); } catch { /* logging is best effort */ }
        }
        _log = WriteLog;

        var registry = new WidgetRegistry();
        var widgetsDir = Path.Combine(AppContext.BaseDirectory, "widgets");
        var result = WidgetAssemblyLoader.LoadInto(registry, widgetsDir);
        foreach (var err in result.Errors) WriteLog("[Warn] widget load: " + err);
        WriteLog($"Loaded {result.Registered} widget type(s) from {widgetsDir}");

        var controller = new HostController(
            registry,
            new FileWidgetStateStore(Path.Combine(dataDir, "state")),
            new JsonLayoutStore(Path.Combine(dataDir, "layout.json")),
            log: WriteLog);

        var window = new MainWindow(controller);
        var snapshot = new JsonLayoutStore(Path.Combine(dataDir, "layout.json")).Load();
        if (snapshot?.HostBounds is { } bounds) window.ApplyBounds(bounds);

        window.Loaded += async (_, _) =>
        {
            try { await controller.RestoreAsync(); }
            catch (Exception ex) { WriteLog("[Error] restore: " + ex); }
        };
        window.Closing += (_, _) => controller.SaveLayoutNow();
        window.Closed += (_, _) =>
        {
            // Blocking here is acceptable: the process is exiting and state must hit disk first.
            controller.ShutdownAsync().GetAwaiter().GetResult();
        };

        app.Run(window);
    }

    private static Action<string>? _log;

    private static void Log(string line) => _log?.Invoke(line);

    /// <summary>--data &lt;dir&gt; overrides the default %AppData%\ModuleDock (used by tests).</summary>
    private static string DataDirectory(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModuleDock");
    }
}
