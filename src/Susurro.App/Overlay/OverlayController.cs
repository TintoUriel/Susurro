using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;

namespace Susurro.App.Overlay;

/// <summary>
/// Orquesta el overlay: un mensaje a la vez, cola acotada, temporizadores de un solo disparo
/// (sin bucles ni sondeo). Los mensajes normales se van solos (con el mouse encima, esperan); los
/// importantes y las imágenes quedan hasta que se los cierra o se los contesta (mientras tanto, los que
/// llegan esperan en la cola). Un clic en un mensaje abre la ventana para contestarle a quien lo mandó.
/// Todo ocurre en el hilo de UI.
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
    private readonly Action<WhisperMessage> _reply;
    private OverlaySettings _settings;
    private OverlayHost? _host;
    private bool _hiding;
    /// <summary>El actual no se va solo (importante o imagen): su "Visto" se envía al cerrarlo.</summary>
    private bool _currentPersistent;
    private DateTime _holdEndsUtc;
    private TimeSpan? _pausedRemaining;

    /// <param name="saveImage">Guarda la imagen del mensaje; devuelve la ruta o null si falló.</param>
    /// <param name="reply">Abre la ventana para contestarle a quien mandó el mensaje.</param>
    public OverlayController(OverlaySettings settings, Action<WhisperMessage> onShown, Action onIdle,
        Func<WhisperMessage, string?> saveImage, Action<WhisperMessage> reply)
    {
        _settings = settings.Clone();
        _onShown = onShown;
        _onIdle = onIdle;
        _saveImage = saveImage;
        _reply = reply;
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
        _pausedRemaining = null;
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
        // Los importantes y las imágenes quedan en pantalla hasta que se los cierra o se los contesta.
        _currentPersistent = next.Urgent || next.IsImage;
        _pausedRemaining = null;
        try
        {
            _host = new OverlayHost(next, settings);
            _host.Dismissed += OnDismissed;
            _host.ReplyRequested += OnReplyRequested;
            _host.HoverChanged += OnHoverChanged;
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

        if (_currentPersistent) return; // "Visto" y cierre, al hacer clic

        NotifyShown(next);

        // Duración configurada + un poco de tiempo de lectura para textos largos (máx. +4 s).
        var extra = Math.Clamp((next.Text.Length - 80) / 40.0, 0, 4);
        StartHold(TimeSpan.FromSeconds(settings.DurationSeconds + extra));
    }

    private void StartHold(TimeSpan duration)
    {
        _hold.Interval = duration;
        _holdEndsUtc = DateTime.UtcNow + duration;
        _hold.Start();
    }

    /// <summary>Con el mouse encima, un mensaje normal no se va: da tiempo a hacerle clic.</summary>
    private void OnHoverChanged(bool inside)
    {
        if (_host == null || _hiding || _currentPersistent) return;
        if (inside)
        {
            if (!_hold.IsEnabled) return;
            _hold.Stop();
            _pausedRemaining = _holdEndsUtc - DateTime.UtcNow;
        }
        else if (_pausedRemaining is { } remaining)
        {
            _pausedRemaining = null;
            StartHold(remaining > TimeSpan.FromSeconds(1.5) ? remaining : TimeSpan.FromSeconds(1.5));
        }
    }

    private void OnDismissed()
    {
        if (_hiding || _host == null) return;
        // Para un importante o una imagen, "Visto" = lo cerraron (el normal ya lo avisó al mostrarse).
        if (_currentPersistent && _queue.Current is { } current) NotifyShown(current);
        EndCurrent();
    }

    private void OnReplyRequested()
    {
        if (_hiding || _host == null || _queue.Current is not { } current) return;
        if (_currentPersistent) NotifyShown(current);
        EndCurrent();
        try { _reply(current); }
        catch (Exception ex) { Log.Error("overlay", "No se pudo abrir la respuesta", ex); }
    }

    private void NotifyShown(WhisperMessage message)
    {
        try { _onShown(message); }
        catch (Exception ex) { Log.Error("overlay", "Error notificando mensaje mostrado", ex); }
    }

    private void EndCurrent()
    {
        _hold.Stop();
        _pausedRemaining = null;
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
