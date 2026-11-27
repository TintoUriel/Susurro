using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Susurro.Core.Logging;
using Susurro.Core.Net;
using Susurro.Core.Protocol;
using Susurro.Core.Transfers;

namespace Susurro.App.Services;

/// <summary>
/// Sesión de control remoto sobre la conexión cifrada existente.
/// <para><b>Lado host</b> (la PC que se deja ver): captura la pantalla y envía frames JPEG,
/// recibe eventos de mouse/teclado y los inyecta.</para>
/// <para><b>Lado viewer</b> (la PC que ve y controla): recibe frames y los muestra,
/// envía eventos de mouse/teclado.</para>
/// No requiere autorización: solo se usa entre las PCs del mismo usuario.
/// </summary>
internal sealed class RemoteSession : IDisposable
{
    private readonly PeerSession _session;
    private readonly bool _isHost;
    private readonly CancellationTokenSource _cts = new();
    private ScreenCapture? _capture;
    private long _frameSeq;
    private bool _disposed;

    public RemoteSession(PeerSession session, bool isHost)
    {
        _session = session;
        _isHost = isHost;
    }

    public string PeerId => _session.PeerId;
    public string PeerName => _session.PeerName;
    public bool IsHost => _isHost;
    public bool IsRunning => !_disposed && !_cts.IsCancellationRequested;

    /// <summary>Frame JPEG recibido (lado viewer): w, h, jpegBytes.</summary>
    public event Action<int, int, byte[]>? FrameReceived;
    /// <summary>La sesión remota terminó (cualquier lado).</summary>
    public event Action? Ended;

    /// <summary>Arranca el streaming de pantalla (solo el host).</summary>
    public void StartHosting()
    {
        if (!_isHost || _disposed) return;
        _capture = new ScreenCapture(40);
        _ = CaptureLoopAsync(_cts.Token);
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        Log.Info("remote", $"Compartiendo pantalla con {_session.PeerName}");
        try
        {
            while (!ct.IsCancellationRequested && !_session.IsClosed)
            {
                var jpeg = _capture?.Capture();
                if (jpeg != null && jpeg.Length > 0)
                {
                    var seq = Interlocked.Increment(ref _frameSeq);
                    var w = _capture!.ScreenWidth;
                    var h = _capture.ScreenHeight;
                    await SendFrameAsync(jpeg, seq, w, h, ct).ConfigureAwait(false);
                }
                await Task.Delay(100, ct).ConfigureAwait(false); // ~10 FPS
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("remote", "Error en el streaming de pantalla", ex);
        }
        finally
        {
            Log.Info("remote", "Streaming de pantalla detenido");
        }
    }

    private async Task SendFrameAsync(byte[] jpeg, long seq, int w, int h, CancellationToken ct)
    {
        var chunkSize = TransferLimits.ChunkSize;
        for (var offset = 0; offset < jpeg.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            if (_session.IsClosed) return;
            var len = Math.Min(chunkSize, jpeg.Length - offset);
            var chunk = new byte[len];
            Buffer.BlockCopy(jpeg, offset, chunk, 0, len);
            var p = new Packet
            {
                T = PacketType.RemoteFrame,
                FrameSeq = seq,
                W = offset == 0 ? w : null,
                H = offset == 0 ? h : null,
                Size = offset == 0 ? jpeg.Length : null,
                Offset = offset,
                Data = chunk,
            };
            if (!await _session.SendAsync(p).ConfigureAwait(false)) return;
        }
    }

    /// <summary>Recibe un paquete de control remoto. Llamar desde el hilo de red.</summary>
    public void HandlePacket(Packet p)
    {
        if (_disposed) return;
        switch (p.T)
        {
            case PacketType.Remote when p.State == RemoteState.End:
                Stop();
                break;
            case PacketType.RemoteFrame when !_isHost:
                HandleFrame(p);
                break;
            case PacketType.RemoteInput when _isHost:
                HandleInput(p);
                break;
        }
    }

    // --- lado viewer: recepción de frames ---

    private long _currentFrameSeq;
    private int _currentW, _currentH;
    private long _currentFrameSize;
    private MemoryStream? _frameBuf;

    private void HandleFrame(Packet p)
    {
        if (p.FrameSeq == null || p.Offset == null || p.Data == null) return;
        var seq = p.FrameSeq.Value;
        var offset = p.Offset.Value;

        if (offset == 0)
        {
            if (p.W == null || p.H == null || p.Size == null) return;
            _currentFrameSeq = seq;
            _currentW = p.W.Value;
            _currentH = p.H.Value;
            _currentFrameSize = p.Size.Value;
            _frameBuf?.Dispose();
            _frameBuf = new MemoryStream((int)_currentFrameSize);
        }

        if (seq != _currentFrameSeq || _frameBuf == null) return;
        if (offset != _frameBuf.Length) return; // fuera de orden — descartar

        _frameBuf.Write(p.Data, 0, p.Data.Length);

        if (_frameBuf.Length >= _currentFrameSize)
        {
            var jpeg = _frameBuf.ToArray();
            _frameBuf.Dispose();
            _frameBuf = null;
            FrameReceived?.Invoke(_currentW, _currentH, jpeg);
        }
    }

    // --- lado host: recepción de input ---

    private void HandleInput(Packet p)
    {
        if (p.InputType == null) return;
        var x = p.X ?? 0;
        var y = p.Y ?? 0;
        switch (p.InputType)
        {
            case RemoteInputType.MouseMove:
                InputInjector.MoveMouse(x, y);
                break;
            case RemoteInputType.MouseDown:
                InputInjector.MoveMouse(x, y);
                InputInjector.MouseButton(p.Btn ?? 0, true);
                break;
            case RemoteInputType.MouseUp:
                InputInjector.MoveMouse(x, y);
                InputInjector.MouseButton(p.Btn ?? 0, false);
                break;
            case RemoteInputType.MouseWheel:
                InputInjector.MouseWheel(p.Delta ?? 0);
                break;
            case RemoteInputType.KeyDown:
                if (p.Vk is int vkd) InputInjector.KeyPress(vkd, true);
                break;
            case RemoteInputType.KeyUp:
                if (p.Vk is int vku) InputInjector.KeyPress(vku, false);
                break;
        }
    }

    // --- envío de input (lado viewer) ---

    public void SendMouseMove(int x, int y)
    {
        if (_isHost || _disposed) return;
        _ = _session.SendAsync(new Packet { T = PacketType.RemoteInput, InputType = RemoteInputType.MouseMove, X = x, Y = y });
    }

    public void SendMouseButton(int x, int y, int button, bool down)
    {
        if (_isHost || _disposed) return;
        _ = _session.SendAsync(new Packet
        {
            T = PacketType.RemoteInput,
            InputType = down ? RemoteInputType.MouseDown : RemoteInputType.MouseUp,
            X = x, Y = y, Btn = button,
        });
    }

    public void SendMouseWheel(int delta)
    {
        if (_isHost || _disposed) return;
        _ = _session.SendAsync(new Packet { T = PacketType.RemoteInput, InputType = RemoteInputType.MouseWheel, Delta = delta });
    }

    public void SendKey(int virtualKey, bool down)
    {
        if (_isHost || _disposed) return;
        _ = _session.SendAsync(new Packet
        {
            T = PacketType.RemoteInput,
            InputType = down ? RemoteInputType.KeyDown : RemoteInputType.KeyUp,
            Vk = virtualKey,
        });
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _capture?.Dispose();
        _frameBuf?.Dispose();
        _ = _session.SendAsync(new Packet { T = PacketType.Remote, State = RemoteState.End });
        Log.Info("remote", $"Sesión remota con {_session.PeerName} terminada");
        Ended?.Invoke();
    }

    public void Dispose()
    {
        if (!_disposed) Stop();
        _cts.Dispose();
    }
}
