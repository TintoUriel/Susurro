using System;
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

        var instance = SingleInstance.TryAcquire(args.Profile);
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
