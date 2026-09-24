using System;
using System.Threading;

namespace Susurro.App.Services;

/// <summary>
/// Una sola instancia por perfil. Si se abre una segunda, le pide a la primera que muestre su
/// ventana (evento con nombre; la primera espera con RegisterWaitForSingleObject: sin sondeo).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>Devuelve null si ya hay otra instancia (a la que se le pidió mostrarse).</summary>
    public static SingleInstance? TryAcquire(string? profile)
    {
        var name = "Susurro" + (profile == null ? "" : "." + profile);
        var mutex = new Mutex(true, @"Local\" + name + ".Instance", out var createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Show");
        if (!createdNew)
        {
            try { evt.Set(); } catch { }
            evt.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, evt);
    }

    public void ListenForShowRequests(Action onShow)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
