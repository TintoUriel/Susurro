using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Susurro.Core.Logging;
using Susurro.Core.Protocol;

namespace Susurro.Core.Net;

/// <summary>Tiempos del latido. El ping solo se envía si la conexión estuvo inactiva.</summary>
public sealed record HeartbeatOptions(TimeSpan CheckInterval, TimeSpan IdleBeforePing, TimeSpan DeadAfter)
{
    public static readonly HeartbeatOptions Default = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));
}

/// <summary>
/// Una conexión TCP ya autenticada y cifrada con otra PC.
/// - Un único bucle de lectura asíncrono (sin hilos bloqueados, sin CPU en reposo).
/// - Envíos serializados con un semáforo.
/// - Latido "solo cuando hace falta": quien marcó envía un ping si no recibió nada en 30 s;
///   cualquiera de los dos da la conexión por muerta tras 75 s sin recibir nada.
/// </summary>
internal sealed class PeerSession
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SecureChannel _channel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly HeartbeatOptions _heartbeat;
    private Timer? _timer;
    private long _lastReceive;
    private long _lastPing;
    private int _closed;

    public PeerSession(Socket socket, NetworkStream stream, SecureChannel channel, bool isDialer, string dialerId,
        string peerId, string peerName, string peerBoot, IPEndPoint remote, int peerPort, long clockOffsetMs, HeartbeatOptions heartbeat)
    {
        _socket = socket;
        _stream = stream;
        _channel = channel;
        IsDialer = isDialer;
        DialerId = dialerId;
        PeerId = peerId;
        PeerName = peerName;
        PeerBoot = peerBoot;
        Remote = remote;
        PeerPort = peerPort;
        ClockOffsetMs = clockOffsetMs;
        _heartbeat = heartbeat;
        _lastReceive = Environment.TickCount64;
    }

    public long Number { get; } = Interlocked.Increment(ref _counter);
    private static long _counter;

    public bool IsDialer { get; }
    public string DialerId { get; }
    public string PeerId { get; }
    public string PeerName { get; set; }
    public string PeerBoot { get; }
    public IPEndPoint Remote { get; }
    public int PeerPort { get; }
    /// <summary>Reloj remoto − reloj local (aprox.), para descartar mensajes muy viejos.</summary>
    public long ClockOffsetMs { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    /// <summary>La sesión ya envió/recibió "ok" y puede transportar mensajes.</summary>
    public bool Ready
    {
        get => Volatile.Read(ref _ready);
        set => Volatile.Write(ref _ready, value);
    }
    private bool _ready;
    public string? CloseReason { get; private set; }
    /// <summary>La otra PC avisó que se cerraba (bye): no hace falta insistir en reconectar.</summary>
    public bool ClosedByPeer { get; private set; }

    public event Action<PeerSession, Packet>? PacketReceived;
    public event Action<PeerSession, string>? Closed;

    public void Start()
    {
        _timer = new Timer(_ => HeartbeatTick(), null, _heartbeat.CheckInterval, _heartbeat.CheckInterval);
        _ = ReadLoopAsync();
    }

    public async Task<bool> SendAsync(Packet packet)
    {
        if (IsClosed) return false;
        try
        {
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }

        try
        {
            if (IsClosed) return false;
            var sealedFrame = _channel.Seal(SusurroJson.Serialize(packet));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await FrameIO.WriteFrameAsync(_stream, sealedFrame, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or CryptographicException)
        {
            Close("error de envío");
            return false;
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Envía un ping inmediato (para comprobar si una sesión sigue viva).</summary>
    public void Probe()
    {
        Interlocked.Exchange(ref _lastPing, Environment.TickCount64);
        _ = SendAsync(new Packet { T = PacketType.Ping, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task SendByeAsync(string reason)
    {
        using var t = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        try { await SendAsync(new Packet { T = PacketType.Bye, Reason = reason }).WaitAsync(t.Token).ConfigureAwait(false); }
        catch { }
        Close(reason);
    }

    private async Task ReadLoopAsync()
    {
        var reason = "conexión cerrada por la otra PC";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await FrameIO.ReadFrameAsync(_stream, ProtocolConstants.MaxSessionFrame, _cts.Token).ConfigureAwait(false);
                if (frame == null) break;

                byte[] plain;
                try { plain = _channel.Open(frame); }
                catch (CryptographicException)
                {
                    reason = "trama no auténtica";
                    break;
                }

                Interlocked.Exchange(ref _lastReceive, Environment.TickCount64);

                Packet packet;
                try { packet = SusurroJson.Deserialize(plain); }
                catch (ProtocolException ex)
                {
                    Log.Warn("net", "Paquete ilegible ignorado: " + ex.Message);
                    continue;
                }

                switch (packet.T)
                {
                    case PacketType.Ping:
                        _ = SendAsync(new Packet { T = PacketType.Pong, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                        break;
                    case PacketType.Pong:
                        break;
                    case PacketType.Bye:
                        ClosedByPeer = true;
                        reason = "la otra PC cerró Susurro" + (string.IsNullOrEmpty(packet.Reason) ? "" : $" ({packet.Reason})");
                        Close(reason);
                        return;
                    default:
                        try { PacketReceived?.Invoke(this, packet); }
                        catch (Exception ex) { Log.Error("net", "Error procesando paquete " + packet.T, ex); }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { reason = CloseReason ?? "cancelada"; }
        catch (ProtocolException ex) { reason = "protocolo: " + ex.Message; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            reason = "conexión perdida";
        }
        catch (Exception ex)
        {
            Log.Error("net", "Error inesperado en lectura", ex);
            reason = "error interno";
        }
        Close(reason);
    }

    private void HeartbeatTick()
    {
        if (IsClosed) return;
        var now = Environment.TickCount64;
        var idle = now - Interlocked.Read(ref _lastReceive);
        if (idle > _heartbeat.DeadAfter.TotalMilliseconds)
        {
            Close("sin respuesta (latido)");
            return;
        }
        if (IsDialer && idle >= _heartbeat.IdleBeforePing.TotalMilliseconds &&
            now - Interlocked.Read(ref _lastPing) >= _heartbeat.IdleBeforePing.TotalMilliseconds)
        {
            Interlocked.Exchange(ref _lastPing, now);
            _ = SendAsync(new Packet { T = PacketType.Ping, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        try { _timer?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
        _ = DisposeChannelWhenIdleAsync();
        try { Closed?.Invoke(this, reason); }
        catch (Exception ex) { Log.Error("net", "Error en cierre de sesión", ex); }
    }

    private async Task DisposeChannelWhenIdleAsync()
    {
        // Espera a que no haya un envío en curso antes de liberar las claves AES.
        try
        {
            if (await _sendLock.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                _sendLock.Release();
        }
        catch { }
        _channel.Dispose();
        _cts.Dispose();
    }
}
