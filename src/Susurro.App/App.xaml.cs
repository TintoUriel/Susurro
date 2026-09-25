using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Susurro.App.Services;
using Susurro.Core.Logging;

namespace Susurro.App;

public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = CommandLine.Parse(e.Args);

        if (args.IsMaintenanceCommand)
        {
            // Comandos para el instalador/desinstalador: sin interfaz.
            if (args.UninstallCleanup) AutoStart.RemoveAll();
            else if (args.SetAutoStart is bool on) AutoStart.Set(on, args.Profile);
            Shutdown(0);
            return;
        }

        if (args.UpdatedFrom is int oldPid) WaitForPreviousVersion(oldPid);

        // Tras una actualización no se le pide a la anterior que se muestre: sería justo lo que se evita.
        var instance = args.UpdatedFrom == null
            ? SingleInstance.TryAcquire(args.Profile)
            : SingleInstance.TryAcquire(args.Profile, showExisting: false, waitForExisting: TimeSpan.FromSeconds(20));
        if (instance == null)
        {
            Shutdown(0); // ya está abierta: se le pidió que muestre su ventana
            return;
        }

        // La aplicación nunca debe cerrarse por una excepción no prevista.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Log.Error("app", "Excepción no controlada (terminando: " + ev.IsTerminating + ")", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Warn("app", "Tarea con excepción no observada", ev.Exception.GetBaseException());
            ev.SetObserved();
        };

        _controller = new AppController(args, instance, Dispatcher);
        _controller.Start();
    }

    /// <summary>
    /// Versión nueva lanzada por la actualización automática: le avisa a la anterior que arrancó (si no,
    /// la anterior vuelve atrás) y espera a que termine para tomar su lugar (instancia única, puerto).
    /// </summary>
    private static void WaitForPreviousVersion(int oldPid)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SingleInstance.UpdateStartedEventName(oldPid), out var started))
            {
                using (started) started.Set();
            }
            using var old = Process.GetProcessById(oldPid);
            old.WaitForExit(20_000);
        }
        catch (ArgumentException)
        {
            // ya terminó
        }
        catch (Exception)
        {
            // sin acceso al proceso: la instancia única decide
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", "Error en la interfaz (recuperado)", e.Exception);
        e.Handled = true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows se apaga o cierra sesión: avisar a la otra PC y salir ordenadamente.
        _controller?.OnSessionEnding();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
