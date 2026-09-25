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

    /// <summary>Devuelve null si ya hay otra instancia (a la que se le pidió mostrarse, si <paramref name="showExisting"/>).</summary>
    /// <param name="waitForExisting">Tras una actualización: cuánto esperar a que la versión anterior termine.</param>
    public static SingleInstance? TryAcquire(string? profile, bool showExisting = true, TimeSpan waitForExisting = default)
    {
        var name = "Susurro" + (profile == null ? "" : "." + profile);
        var mutex = new Mutex(true, @"Local\" + name + ".Instance", out var createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Show");
        if (!createdNew && waitForExisting > TimeSpan.Zero)
        {
            try
            {
                createdNew = mutex.WaitOne(waitForExisting);
            }
            catch (AbandonedMutexException)
            {
                createdNew = true; // la anterior terminó sin liberarlo: ahora es de esta instancia
            }
        }
        if (!createdNew)
        {
            if (showExisting)
            {
                try { evt.Set(); } catch { }
            }
            evt.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, evt);
    }

    /// <summary>
    /// Evento con el que la versión nueva avisa a la anterior que arrancó bien
    /// (ver <see cref="AppController"/>, reinicio por actualización).
    /// </summary>
    public static string UpdateStartedEventName(int oldProcessId) => @"Local\Susurro.Updated." + oldProcessId;

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
