using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;

namespace Susurro.App.Overlay;

/// <summary>
/// Orquesta el overlay: un mensaje a la vez, cola acotada, temporizadores de un solo disparo
/// (sin bucles ni sondeo). Los mensajes normales se van solos; los importantes quedan hasta que
/// se les hace clic (mientras tanto, los que llegan esperan en la cola). Todo ocurre en el hilo de UI.
/// </summary>
internal sealed class OverlayController
{
    private static readonly TimeSpan GapBetweenMessages = TimeSpan.FromMilliseconds(220);

    private readonly DisplayQueue _queue = new(20);
    private readonly Dictionary<string, OverlaySettings> _overrides = new();
    private readonly DispatcherTimer _hold;
    private readonly DispatcherTimer _gap;
    private readonly Action<WhisperMessage> _onShown;
    private readonly Action _onIdle;
    private readonly Func<WhisperMessage, string?> _saveImage;
    private OverlaySettings _settings;
    private OverlayHost? _host;
    private bool _hiding;

    /// <param name="saveImage">Guarda la imagen del mensaje; devuelve la ruta o null si falló.</param>
    public OverlayController(OverlaySettings settings, Action<WhisperMessage> onShown, Action onIdle, Func<WhisperMessage, string?> saveImage)
    {
        _settings = settings.Clone();
        _onShown = onShown;
        _onIdle = onIdle;
        _saveImage = saveImage;
        _hold = new DispatcherTimer(DispatcherPriority.Normal);
        _hold.Tick += (_, _) => EndCurrent();
        _gap = new DispatcherTimer(DispatcherPriority.Normal) { Interval = GapBetweenMessages };
        _gap.Tick += (_, _) =>
        {
            _gap.Stop();
            TryShowNext();
        };
    }

    public void UpdateSettings(OverlaySettings settings) => _settings = settings.Clone();

    /// <summary>No hay nada en pantalla ni esperando para mostrarse.</summary>
    public bool IsIdle => _host == null && !_hiding && !_gap.IsEnabled && _queue.PendingCount == 0;

    public void Enqueue(WhisperMessage message)
    {
        var dropped = _queue.Enqueue(message);
        if (dropped != null)
        {
            _overrides.Remove(dropped.Id);
            Log.Warn("overlay", "Cola de mensajes llena: se descartó el más antiguo");
        }
        TryShowNext();
    }

    /// <summary>Mensaje de prueba local con la configuración indicada (sin guardar).</summary>
    public void ShowTest(OverlaySettings preview, bool urgent, string senderName)
    {
        var text = urgent ? "VENÍ A LA OFICINA" : "Traé los papeles cuando puedas";
        var msg = new WhisperMessage(MessageRules.NewMessageId(), text, senderName, DateTimeOffset.UtcNow, urgent, 0, false, IsTest: true);
        _overrides[msg.Id] = preview.Clone();
        Enqueue(msg);
    }

    /// <summary>Oculta todo inmediatamente (al salir).</summary>
    public void Clear()
    {
        _hold.Stop();
        _gap.Stop();
        _queue.Clear();
        _overrides.Clear();
        _host?.Dispose();
        _host = null;
        _hiding = false;
    }

    private void TryShowNext()
    {
        if (_host != null || _hiding || _gap.IsEnabled) return;
        var next = _queue.BeginNext();
        if (next == null)
        {
            _onIdle();
            return;
        }

        var settings = _overrides.Remove(next.Id, out var preview) ? preview : _settings;
        // Los importantes y las imágenes quedan en pantalla hasta que se les hace clic.
        var clickToClose = next.Urgent || next.IsImage;
        try
        {
            _host = new OverlayHost(next, settings);
            if (clickToClose) _host.Dismissed += OnDismissed;
            if (next.IsImage)
            {
                var host = _host;
                host.SaveRequested += () =>
                {
                    var path = _saveImage(next);
                    host.MarkSaved(path != null ? "Guardada en Descargas ✓" : "No se pudo guardar");
                };
            }
            _host.Show();
        }
        catch (Exception ex)
        {
            Log.Error("overlay", "No se pudo mostrar el overlay", ex);
            _host?.Dispose();
            _host = null;
            _queue.CompleteCurrent();
            _gap.Start();
            return;
        }

        if (clickToClose) return; // "Visto" y cierre, al hacer clic

        NotifyShown(next);

        // Duración configurada + un poco de tiempo de lectura para textos largos (máx. +4 s).
        var extra = Math.Clamp((next.Text.Length - 80) / 40.0, 0, 4);
        _hold.Interval = TimeSpan.FromSeconds(settings.DurationSeconds + extra);
        _hold.Start();
    }

    private void OnDismissed()
    {
        if (_hiding || _host == null) return;
        if (_queue.Current is { } current) NotifyShown(current); // para un importante, "Visto" = le hicieron clic
        EndCurrent();
    }

    private void NotifyShown(WhisperMessage message)
    {
        try { _onShown(message); }
        catch (Exception ex) { Log.Error("overlay", "Error notificando mensaje mostrado", ex); }
    }

    private void EndCurrent()
    {
        _hold.Stop();
        var host = _host;
        if (host == null)
        {
            _queue.CompleteCurrent();
            TryShowNext();
            return;
        }
        _hiding = true;
        host.Hide(() =>
        {
            _hiding = false;
            if (ReferenceEquals(_host, host)) _host = null;
            _queue.CompleteCurrent();
            if (_queue.PendingCount > 0) _gap.Start();
            else _onIdle();
        });
    }
}
