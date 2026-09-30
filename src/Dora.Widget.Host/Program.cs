using System.IO;
using System.Windows;
using Dora.Widget.Abstractions;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host;

public static class Program
{
    private const long MaxLogBytes = 1_000_000;

    [STAThread]
    public static void Main(string[] args)
    {
        var dataDir = DataDirectory(args);
        Directory.CreateDirectory(dataDir);

        // One Host per data folder: a second launch just brings the running one forward.
        using var single = SingleInstance.Acquire(dataDir);
        if (single is null) return;

        var logFile = Path.Combine(dataDir, "host.log");
        RotateLog(logFile);
        void WriteLog(string line)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:s} {line}{Environment.NewLine}"); } catch { /* logging is best effort */ }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var errors = new ErrorReporter(WriteLog,
            notifyUser: msg => MessageBox.Show(msg, "ModuleDock", MessageBoxButton.OK, MessageBoxImage.Error));
        app.DispatcherUnhandledException += (_, e) =>
        {
            errors.Report("UI", e.Exception);
            e.Handled = true; // a widget's bug must not take the whole Host down
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            errors.Report("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            errors.Report("Task", e.Exception);
            e.SetObserved();
        };

        var registry = new WidgetRegistry();
        var widgetsDir = Path.Combine(AppContext.BaseDirectory, "widgets");
        var detachHandlers = new List<IWidgetDetachHandler>();
        var result = WidgetAssemblyLoader.LoadInto(registry, widgetsDir, detachHandlers);
        foreach (var err in result.Errors) WriteLog("[Warn] widget load: " + err);
        foreach (var w in result.Warnings) WriteLog("[Info] widget manifest: " + w);
        WriteLog($"Loaded {result.Registered} widget type(s) from {widgetsDir}");
        foreach (var h in detachHandlers) WriteLog($"[Info] hand-over supported for {h.WidgetId}");

        // Interaction tuning lives in the same settings file; the options object is shared and read live.
        var settingsStore = new HostSettingsStore(Path.Combine(dataDir, "host-settings.json"));
        var options = new HostOptions();
        var interaction = settingsStore.LoadInteraction();
        interaction.ApplyTo(options);

        var controller = new HostController(
            registry,
            new FileWidgetStateStore(Path.Combine(dataDir, "state")),
            new JsonLayoutStore(Path.Combine(dataDir, "layout.json")),
            options: options,
            log: WriteLog,
            grants: new JsonPermissionGrantStore(Path.Combine(dataDir, "permissions.json")),
            detachHandlers: detachHandlers);

        var window = new MainWindow(controller);

        // Edge snap / auto-hide: Host-owned window behaviour, independent of any widget.
        var placement = new HostWindowController(
            window, settingsStore.Load(), new SystemMonitorProvider(), new DispatcherTimerScheduler(),
            persist: s =>
            {
                try { settingsStore.Save(s); } catch (Exception ex) { WriteLog("[Error] host settings save: " + ex.Message); }
            });
        window.AttachPlacement(placement);
        window.AttachInteraction(interaction, updated =>
        {
            updated.ApplyTo(options);
            controller.Panel.RequestRelayout();
            try { settingsStore.SaveInteraction(updated); } catch (Exception ex) { WriteLog("[Error] interaction settings save: " + ex.Message); }
        });
        placement.Initialize();
        single.ActivationRequested += () => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            placement.Machine.Reveal();
            if (window.IsVisible)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
        }));

        window.Loaded += async (_, _) =>
        {
            try { await controller.RestoreAsync(); }
            catch (Exception ex) { errors.Report("restore", ex); }
        };
        window.Closing += (_, _) =>
        {
            placement.Flush();
            controller.SaveLayoutNow();
        };
        window.Closed += (_, _) =>
        {
            placement.Dispose();
            // State must reach disk before the process exits, but a widget that never finishes its
            // shutdown must not keep the Host alive forever.
            try
            {
                if (!UiTasks.WaitPumping(controller.ShutdownAsync(), TimeSpan.FromSeconds(5)))
                    WriteLog("[Warn] widget shutdown timed out after 5s");
            }
            catch (Exception ex) { errors.Report("shutdown", ex); }
        };

        using var watchdog = new UiWatchdog(window.Dispatcher, WriteLog);
        app.Run(window);
    }

    /// <summary>Keeps the log from growing without bound: the previous log is kept as host.log.1.</summary>
    internal static void RotateLog(string logFile)
    {
        try
        {
            var info = new FileInfo(logFile);
            if (!info.Exists || info.Length < MaxLogBytes) return;
            File.Move(logFile, logFile + ".1", overwrite: true);
        }
        catch (IOException) { /* rotation is best effort */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>--data &lt;dir&gt; overrides the default %AppData%\ModuleDock (used by tests).</summary>
    private static string DataDirectory(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModuleDock");
    }
}
