using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dora.Widget.Host;

/// <summary>
/// Allows one Host per data folder. A second launch does not start; it asks the running Host to come
/// forward instead (which also reveals an auto-hidden Host). Two Hosts writing the same layout,
/// state and settings files would corrupt each other.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly Thread _listener;
    private volatile bool _stop;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _listener = new Thread(Listen) { IsBackground = true, Name = "ModuleDock single-instance listener" };
        _listener.Start();
    }

    /// <summary>Raised on a background thread when another launch asked this Host to come forward.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Returns the guard when this is the first Host for <paramref name="dataDirectory"/>; otherwise signals
    /// the running one and returns null (the caller should exit).
    /// </summary>
    public static SingleInstance? Acquire(string dataDirectory)
    {
        var name = NameFor(dataDirectory);
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                using var signal = EventWaitHandle.OpenExisting(name + ".activate");
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* the first Host is still starting; nothing to wake yet */ }
            return null;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
        return new SingleInstance(mutex, activate);
    }

    /// <summary>Per data folder, per Windows session; the path is hashed so it is a valid kernel object name.</summary>
    internal static string NameFor(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return @"Local\ModuleDock." + hash;
    }

    private void Listen()
    {
        while (!_stop)
        {
            try
            {
                if (_activate.WaitOne(300) && !_stop) ActivationRequested?.Invoke();
            }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _listener.Join(1000);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        _mutex.Dispose();
    }
}
