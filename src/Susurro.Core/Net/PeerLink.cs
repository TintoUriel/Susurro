using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Discovery;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;
using Susurro.Core.Pairing;
using Susurro.Core.Protocol;

namespace Susurro.Core.Net;

public enum LinkStatus { NotPaired, Disconnected, Connecting, Connected }

public sealed record LinkState(LinkStatus Status, string? PeerName, string? RemoteEndPoint, string? Detail);

public sealed record SendResult(bool Accepted, string? MessageId, string? Error);

public sealed record PairingResult(bool Success, string? Error, PeerSettings? Peer)
{
    public static PairingResult Fail(string error) => new(false, error, null);
}

public sealed class PeerLinkOptions
{
    public required string InstanceId { get; init; }
    public required string LocalName { get; init; }
    public int Port { get; init; } = AppSettings.DefaultPort;
    public int DiscoveryPort { get; init; } = AppSettings.DefaultDiscoveryPort;
    public bool EnableDiscovery { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan[]? BackoffSteps { get; init; }
    public HeartbeatOptions Heartbeat { get; init; } = HeartbeatOptions.Default;
    /// <summary>Tiempo máximo que un mensaje espera en la bandeja de salida sin conexión.</summary>
    public TimeSpan OutboxTtl { get; init; } = TimeSpan.FromMinutes(2);
    public int OutboxCapacity { get; init; } = 20;
    /// <summary>Mensajes recibidos con más antigüedad que esta (según el reloj del remitente corregido) se descartan.</summary>
    public TimeSpan StaleMessageAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Núcleo de comunicación entre las dos PCs (P2P, sin servidor):
///  - Escucha TCP para conexiones entrantes (sesión o vinculación).
///  - Marca hacia la otra PC cuando no hay sesión, con espera creciente y descubrimiento UDP.
///  - Autentica con la clave de vínculo, cifra con AES-GCM, arbitra conexiones duplicadas.
///  - Bandeja de salida con reintento tras reconexión, confirmaciones y filtro de duplicados.
/// Todos los eventos se disparan desde hilos del pool: la UI debe pasarlos a su dispatcher.
/// </summary>
public sealed class PeerLink : IAsyncDisposable
{
    private const int MaxPendingHandshakes = 4;

    private readonly PeerLinkOptions _opts;
    private readonly IKeyProtector _protector;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _pairLock = new(1, 1);
    private readonly DuplicateFilter _dupes = new(512);
    private readonly List<OutMsg> _outbox = new();
    private readonly Timer _outboxTimer;
    private readonly Timer _networkTimer;
    private readonly DiscoveryService? _discovery;
    private readonly CancellationTokenSource _cts = new();

    private volatile string _localName;
    private PeerSettings? _peer;
    private byte[]? _peerKey;
    private PeerSession? _session;
    private PairingInvitation? _invitation;
    private IPEndPoint? _hint;
    private long _seq;
    private int _pendingHandshakes;
    private int _consecutiveFailures;
    private bool _dialing;
    private string? _dialDetail;
    private string? _listenerError;
    private bool _peerRejectedUs;
    private bool _firewallSuspect;
    private bool _started;
    private bool _stopped;
    private LinkState? _lastState;
    private long _lastUnknownLog = -1_000_000;

    public PeerLink(PeerLinkOptions options, IKeyProtector protector)
    {
        _opts = options;
        _protector = protector;
        _localName = options.LocalName;
        BootId = HandshakeCrypto.NewBootId();
        _outboxTimer = new Timer(_ => ExpireOutbox(), null, Timeout.Infinite, Timeout.Infinite);
        _networkTimer = new Timer(_ => OnNetworkSettled(), null, Timeout.Infinite, Timeout.Infinite);
        if (options.EnableDiscovery)
        {
            _discovery = new DiscoveryService(options.InstanceId, options.DiscoveryPort, DescribeSelf);
            _discovery.PeerSeen += OnPeerSeen;
        }
    }

    // ------------------------------------------------------------------ eventos / estado

    public event Action<LinkState>? StateChanged;
    public event Action<WhisperMessage>? MessageReceived;
    public event Action<string, DeliveryState>? DeliveryChanged;
    /// <summary>Cambió el vínculo o datos de la otra PC (nombre, última IP): hay que persistirlo.</summary>
    public event Action<PeerSettings?>? PeerChanged;
    /// <summary>El código activo se cerró (usado, expirado o agotado).</summary>
    public event Action? InvitationClosed;

    public string InstanceId => _opts.InstanceId;
    public string BootId { get; }
    public int Port => _opts.Port;
    public string LocalName => _localName;

    public LinkState State => ComputeState();

    public PeerSettings? Peer
    {
        get { lock (_gate) return _peer?.Clone(); }
    }

    public PairingInvitation? ActiveInvitation
    {
        get { lock (_gate) return _invitation is { } i && i.IsValid(DateTime.UtcNow) ? i : null; }
    }

    // ------------------------------------------------------------------ ciclo de vida

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true;
        }
        Log.Info("net", $"Iniciando: puerto TCP {_opts.Port}, descubrimiento UDP {(_discovery != null ? _opts.DiscoveryPort.ToString() : "desactivado")}");
        _ = ListenLoopAsync(_cts.Token);
        if (_discovery != null)
        {
            _discovery.Start();
            _discovery.Announce();
        }
        _ = DialLoopAsync(_cts.Token);
        PublishState();
    }

    public async Task StopAsync()
    {
        PeerSession? s;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            s = _session;
        }
        if (s != null)
        {
            try { await s.SendByeAsync("cierre").ConfigureAwait(false); } catch { }
        }
        try { _cts.Cancel(); } catch { }
        _discovery?.Dispose();
        _outboxTimer.Dispose();
        _networkTimer.Dispose();
        Log.Info("net", "Comunicación detenida");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Configura (o quita) la PC vinculada desde la configuración guardada.</summary>
    public void SetPeer(PeerSettings? peer)
    {
        byte[]? key = null;
        if (peer != null)
        {
            key = _protector.Unprotect(peer.ProtectedKey);
            if (key is not { Length: 32 })
            {
                Log.Warn("pairing", "No se pudo recuperar la clave de vínculo (¿otro usuario de Windows o archivo copiado?). Hay que volver a vincular.");
                peer = null;
                key = null;
                PeerChanged?.Invoke(null);
            }
        }
        ReplacePeer(peer, key);
    }

    public void Unpair()
    {
        Log.Info("pairing", "Vínculo eliminado por el usuario");
        ReplacePeer(null, null);
        PeerChanged?.Invoke(null);
    }

    public void UpdateLocalName(string name)
    {
        _localName = SettingsValidator.CleanName(name);
        var s = ReadySession();
        if (s != null) _ = s.SendAsync(new Packet { T = PacketType.Profile, Name = _localName });
    }

    public void UpdateManualAddress(string? address)
    {
        PeerSettings? copy;
        lock (_gate)
        {
            if (_peer == null) return;
            _peer.ManualAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            copy = _peer.Clone();
        }
        PeerChanged?.Invoke(copy);
        Kick();
    }

    /// <summary>Cambió la red local (IP, Wi-Fi, cable) o el equipo volvió de suspensión.</summary>
    public void NotifyNetworkChanged()
    {
        try { _networkTimer.Change(_opts.NetworkChangeDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Forzar un intento de conexión inmediato (p. ej. botón "Reconectar").</summary>
    public void ReconnectNow()
    {
        lock (_gate) _consecutiveFailures = 0;
        Kick();
    }

    private void OnNetworkSettled()
    {
        if (_cts.IsCancellationRequested) return;
        Log.Info("net", "Cambio de red detectado: " + NetworkInfo.DescribeLocalAddresses());
        _discovery?.Restart();
        _discovery?.Announce();
        ReadySession()?.Probe();
        lock (_gate) _consecutiveFailures = 0;
        Kick();
    }

    // ------------------------------------------------------------------ envío de mensajes

    public SendResult Send(string text, bool urgent, bool wantReceipt)
    {
        if (!MessageRules.TryValidate(text, out var clean, out var error))
            return new SendResult(false, null, error);

        OutMsg msg;
        bool connected;
        lock (_gate)
        {
            if (_stopped) return new SendResult(false, null, "Susurro se está cerrando.");
            if (_peer == null) return new SendResult(false, null, "No hay ninguna PC vinculada.");
            if (_outbox.Count >= _opts.OutboxCapacity) return new SendResult(false, null, "Demasiados mensajes en espera.");
            var id = MessageRules.NewMessageId();
            msg = new OutMsg(id, new Packet
            {
                T = PacketType.Message,
                MsgId = id,
                Seq = ++_seq,
                Text = clean,
                Urgent = urgent ? true : null,
                Receipt = wantReceipt ? true : null,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Name = _localName,
            }, DateTime.UtcNow);
            _outbox.Add(msg);
            connected = _session is { Ready: true, IsClosed: false };
        }

        if (!connected)
        {
            Log.Info("msg", $"Mensaje en espera (sin conexión) [{msg.Id[..8]}]");
            DeliveryChanged?.Invoke(msg.Id, DeliveryState.Queued);
        }
        EnsureOutboxTimer();
        _ = FlushOutboxAsync();
        return new SendResult(true, msg.Id, null);
    }

    /// <summary>El overlay mostró el mensaje: si el remitente lo pidió, se le avisa ("Visto").</summary>
    public void ReportShown(WhisperMessage message)
    {
        if (message.IsTest || !message.WantsReceipt) return;
        var s = ReadySession();
        if (s != null) _ = s.SendAsync(new Packet { T = PacketType.Ack, MsgId = message.Id, State = AckState.Shown });
    }

    private async Task FlushOutboxAsync()
    {
        try { await _flushLock.WaitAsync(_cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            var s = ReadySession();
            if (s == null) return;
            List<OutMsg> pending;
            lock (_gate) pending = _outbox.Where(m => m.SentOnSession == 0).ToList();
            foreach (var m in pending)
            {
                if (!await s.SendAsync(m.Packet).ConfigureAwait(false)) break;
                bool stillQueued;
                lock (_gate)
                {
                    stillQueued = _outbox.Contains(m) && m.SentOnSession == 0;
                    if (stillQueued) m.SentOnSession = s.Number;
                }
                if (!stillQueued) continue;
                Log.Info("msg", $"Mensaje enviado [{m.Id[..8]}] ({m.Packet.Text?.Length ?? 0} caracteres{(m.Packet.Urgent == true ? ", urgente" : "")})");
                DeliveryChanged?.Invoke(m.Id, DeliveryState.Sent);
            }
        }
        catch (Exception ex)
        {
            Log.Error("msg", "Error al enviar la bandeja de salida", ex);
        }
        finally
        {
            try { _flushLock.Release(); } catch { }
        }
    }

    private void EnsureOutboxTimer()
    {
        try { _outboxTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }
    }

    private void ExpireOutbox()
    {
        List<OutMsg> expired;
        bool empty;
        lock (_gate)
        {
            var limit = DateTime.UtcNow - _opts.OutboxTtl;
            expired = _outbox.Where(m => m.CreatedUtc < limit).ToList();
            foreach (var m in expired) _outbox.Remove(m);
            empty = _outbox.Count == 0;
        }
        if (empty)
        {
            try { _outboxTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
        foreach (var m in expired)
        {
            Log.Warn("msg", $"Mensaje no entregado: expiró sin conexión [{m.Id[..8]}]");
            DeliveryChanged?.Invoke(m.Id, DeliveryState.Failed);
        }
    }

    private void FailWholeOutbox()
    {
        List<OutMsg> all;
        lock (_gate)
        {
            all = _outbox.ToList();
            _outbox.Clear();
        }
        foreach (var m in all) DeliveryChanged?.Invoke(m.Id, DeliveryState.Failed);
    }

    // ------------------------------------------------------------------ paquetes de sesión

    private void OnSessionPacket(PeerSession session, Packet p)
    {
        switch (p.T)
        {
            case PacketType.Message:
                HandleIncomingMessage(session, p);
                break;
            case PacketType.Ack:
                HandleAck(p);
                break;
            case PacketType.Profile:
                var name = SettingsValidator.CleanName(p.Name);
                if (name.Length > 0) UpdatePeerInfo(session, name);
                break;
        }
    }

    private void HandleIncomingMessage(PeerSession session, Packet p)
    {
        if (!MessageRules.IsValidMessageId(p.MsgId))
        {
            Log.Warn("msg", "Mensaje sin identificador válido descartado");
            return;
        }
        var id = p.MsgId!;
        var text = MessageRules.Sanitize(p.Text);
        var isNew = _dupes.TryRegister(id);

        // Siempre se confirma la recepción (también los duplicados) para que el remitente deje de reintentar.
        _ = session.SendAsync(new Packet { T = PacketType.Ack, MsgId = id, State = AckState.Received });

        if (!isNew)
        {
            Log.Info("msg", $"Mensaje duplicado ignorado [{id[..8]}]");
            return;
        }
        if (text.Length == 0 || text.Length > MessageRules.MaxLength)
        {
            Log.Warn("msg", $"Mensaje inválido descartado [{id[..8]}] (longitud {text.Length})");
            return;
        }

        var sentAt = p.Ts is long ts
            ? DateTimeOffset.FromUnixTimeMilliseconds(ts - session.ClockOffsetMs)
            : DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - sentAt > _opts.StaleMessageAge)
        {
            Log.Warn("msg", $"Mensaje demasiado antiguo descartado [{id[..8]}]");
            return;
        }

        var sender = SettingsValidator.CleanName(p.Name);
        if (sender.Length == 0) sender = session.PeerName;
        var message = new WhisperMessage(id, text, sender, sentAt, p.Urgent == true, p.Seq ?? 0, p.Receipt == true);
        Log.Info("msg", $"Mensaje recibido [{id[..8]}] ({text.Length} caracteres{(message.Urgent ? ", urgente" : "")})");
        MessageReceived?.Invoke(message);
    }

    private void HandleAck(Packet p)
    {
        if (!MessageRules.IsValidMessageId(p.MsgId)) return;
        var id = p.MsgId!;
        if (p.State == AckState.Received)
        {
            bool removed;
            bool empty;
            lock (_gate)
            {
                removed = _outbox.RemoveAll(m => m.Id == id) > 0;
                empty = _outbox.Count == 0;
            }
            if (empty)
            {
                try { _outboxTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
            if (removed)
            {
                Log.Info("msg", $"Mensaje entregado [{id[..8]}]");
                DeliveryChanged?.Invoke(id, DeliveryState.Delivered);
            }
        }
        else if (p.State == AckState.Shown)
        {
            DeliveryChanged?.Invoke(id, DeliveryState.Shown);
        }
    }

    // ------------------------------------------------------------------ gestión de sesiones

    private PeerSession? ReadySession()
    {
        lock (_gate) return _session is { Ready: true, IsClosed: false } s ? s : null;
    }

    private bool TryActivate(PeerSession session)
    {
        PeerSession? replaced = null;
        PeerSession? probe = null;
        lock (_gate)
        {
            if (_stopped || _peer == null) return false;
            if (_session != null && !_session.IsClosed)
            {
                if (!SessionArbiter.ShouldReplace(_session.DialerId, session.DialerId, _opts.InstanceId, _peer.InstanceId))
                {
                    probe = _session;
                }
                else
                {
                    replaced = _session;
                }
            }
            if (probe == null)
            {
                _session = session;
                session.PacketReceived += OnSessionPacket;
                session.Closed += OnSessionClosed;
            }
        }
        if (probe != null)
        {
            // Nos quedamos con la conexión preferida, pero comprobamos que siga viva.
            probe.Probe();
            return false;
        }
        replaced?.Close("reemplazada por una conexión más reciente");
        if (replaced != null) RequeueSentOn(replaced);
        return true;
    }

    private void MarkReady(PeerSession session)
    {
        session.Ready = true;
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _peerRejectedUs = false;
            _firewallSuspect = false;
            _dialDetail = null;
        }
        UpdatePeerInfo(session, session.PeerName);
        Log.Info("net", $"Conectado con {session.PeerName} ({session.Remote.Address}, {(session.IsDialer ? "saliente" : "entrante")})");
        PublishState();
        _ = FlushOutboxAsync();
    }

    private void OnSessionClosed(PeerSession session, string reason)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = ReferenceEquals(_session, session);
            if (wasActive) _session = null;
        }
        session.PacketReceived -= OnSessionPacket;
        session.Closed -= OnSessionClosed;
        RequeueSentOn(session);
        if (!wasActive) return;
        if (session.Ready) Log.Info("net", "Desconectado: " + reason);
        PublishState();
        if (!_cts.IsCancellationRequested) Kick();
    }

    private void RequeueSentOn(PeerSession session)
    {
        List<OutMsg> requeued;
        lock (_gate)
        {
            requeued = _outbox.Where(m => m.SentOnSession == session.Number).ToList();
            foreach (var m in requeued) m.SentOnSession = 0;
        }
        foreach (var m in requeued) DeliveryChanged?.Invoke(m.Id, DeliveryState.Queued);
        if (requeued.Count > 0) _ = FlushOutboxAsync();
    }

    private void UpdatePeerInfo(PeerSession session, string peerName)
    {
        PeerSettings? changed = null;
        lock (_gate)
        {
            if (_peer == null || !ReferenceEquals(_session, session)) return;
            var address = session.Remote.Address.ToString();
            var port = session.IsDialer ? session.Remote.Port : session.PeerPort;
            if (peerName.Length > 0 && _peer.Name != peerName) { _peer.Name = peerName; changed = _peer; }
            if (_peer.LastAddress != address) { _peer.LastAddress = address; changed = _peer; }
            if (port is > 0 and <= 65535 && _peer.LastPort != port) { _peer.LastPort = port; changed = _peer; }
            session.PeerName = _peer.Name;
            changed = changed?.Clone();
        }
        if (changed != null)
        {
            PeerChanged?.Invoke(changed);
            PublishState();
        }
    }

    private void ReplacePeer(PeerSettings? peer, byte[]? key)
    {
        PeerSession? old;
        lock (_gate)
        {
            old = _session;
            _session = null;
            _peer = peer?.Clone();
            if (_peerKey != null) CryptographicOperations.ZeroMemory(_peerKey);
            _peerKey = key;
            _hint = null;
            _peerRejectedUs = false;
            _firewallSuspect = false;
            _consecutiveFailures = 0;
        }
        old?.Close("cambio de vínculo");
        FailWholeOutbox();
        PublishState();
        Kick();
    }

    // ------------------------------------------------------------------ escucha (entrantes)

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, _opts.Port);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(8);
                if (_listenerError != null) Log.Info("net", $"Puerto {_opts.Port} disponible nuevamente");
                lock (_gate) _listenerError = null;
                retryDelay = TimeSpan.FromSeconds(2);
                PublishState();

                while (!ct.IsCancellationRequested)
                {
                    Socket socket;
                    try
                    {
                        socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        continue; // una conexión abortada antes de aceptarse
                    }
                    _ = HandleInboundAsync(socket, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                var msg = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                    ? $"El puerto {_opts.Port} está en uso por otro programa"
                    : $"No se pudo escuchar en el puerto {_opts.Port}";
                bool first;
                lock (_gate)
                {
                    first = _listenerError != msg;
                    _listenerError = msg;
                }
                if (first) Log.Error("net", msg, ex);
                PublishState();
            }
            catch (Exception ex)
            {
                Log.Error("net", "Error en la escucha TCP", ex);
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }

            try { await Task.Delay(retryDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            retryDelay = TimeSpan.FromSeconds(Math.Min(60, retryDelay.TotalSeconds * 2));
        }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _pendingHandshakes) > MaxPendingHandshakes)
        {
            Interlocked.Decrement(ref _pendingHandshakes);
            try { socket.Dispose(); } catch { }
            return;
        }

        var handedOff = false;
        IPEndPoint? remote = null;
        try
        {
            socket.NoDelay = true;
            remote = (IPEndPoint)socket.RemoteEndPoint!;
            var stream = new NetworkStream(socket, ownsSocket: false);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(_opts.HandshakeTimeout);

            var hello = await ReadPlainAsync(stream, hs.Token).ConfigureAwait(false);
            if (hello.T != PacketType.Hello) throw new ProtocolException("Se esperaba hello");
            if (hello.V != ProtocolConstants.Version)
            {
                await SendPlainAsync(stream, Reject(RejectReason.Version), hs.Token).ConfigureAwait(false);
                Log.Warn("net", $"Conexión de {remote.Address} con versión de protocolo incompatible ({hello.V})");
                return;
            }
            if (!SettingsValidator.IsValidInstanceId(hello.Id) || hello.Nonce is not { Length: HandshakeCrypto.NonceSize })
                throw new ProtocolException("hello inválido");
            if (hello.Id == _opts.InstanceId)
            {
                await SendPlainAsync(stream, Reject(RejectReason.Self), hs.Token).ConfigureAwait(false);
                return;
            }

            if (hello.Mode == HelloMode.Pair)
            {
                await HostPairingAsync(stream, hello, remote, hs.Token).ConfigureAwait(false);
                return;
            }
            if (hello.Mode != HelloMode.Session) throw new ProtocolException("Modo desconocido");

            handedOff = await AcceptSessionAsync(socket, stream, hello, remote, hs.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) Log.Warn("net", $"Tiempo de espera agotado en el saludo de {remote?.Address}");
        }
        catch (ProtocolException ex)
        {
            Log.Warn("net", $"Conexión inválida de {remote?.Address}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            // la otra parte cortó durante el saludo
        }
        catch (Exception ex)
        {
            Log.Error("net", "Error inesperado en conexión entrante", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingHandshakes);
            if (!handedOff)
            {
                try { socket.Shutdown(SocketShutdown.Both); } catch { }
                try { socket.Dispose(); } catch { }
            }
        }
    }

    private async Task<bool> AcceptSessionAsync(Socket socket, NetworkStream stream, Packet hello, IPEndPoint remote, CancellationToken ct)
    {
        PeerSettings? peer;
        byte[]? key;
        lock (_gate)
        {
            peer = _peer;
            key = _peerKey;
        }
        if (peer == null || key == null || hello.Id != peer.InstanceId)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastUnknownLog) > 300_000)
            {
                Interlocked.Exchange(ref _lastUnknownLog, now);
                Log.Warn("net", $"Conexión rechazada de {remote.Address}: instancia no vinculada");
            }
            await SendPlainAsync(stream, Reject(RejectReason.Unknown), ct).ConfigureAwait(false);
            return false;
        }

        var nonceD = hello.Nonce!;
        var nonceL = HandshakeCrypto.NewNonce();
        await SendPlainAsync(stream, new Packet
        {
            T = PacketType.Welcome,
            V = ProtocolConstants.Version,
            Mode = HelloMode.Session,
            Id = _opts.InstanceId,
            Name = _localName,
            Boot = BootId,
            Nonce = nonceL,
            Proof = HandshakeCrypto.SessionProof(key, 'L', hello.Id!, _opts.InstanceId, nonceD, nonceL),
            Port = _opts.Port,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, ct).ConfigureAwait(false);

        var auth = await ReadPlainAsync(stream, ct).ConfigureAwait(false);
        var expected = HandshakeCrypto.SessionProof(key, 'D', hello.Id!, _opts.InstanceId, nonceD, nonceL);
        if (auth.T != PacketType.Auth || !HandshakeCrypto.FixedTimeEquals(expected, auth.Proof))
        {
            Log.Warn("net", $"Autenticación fallida desde {remote.Address} (la clave de vínculo no coincide)");
            await SendPlainAsync(stream, Reject(RejectReason.Auth), ct).ConfigureAwait(false);
            return false;
        }

        var channel = SecureChannel.Create(key, isDialer: false, nonceD, nonceL);
        var offset = hello.Ts is long ts ? ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
        var session = new PeerSession(socket, stream, channel, isDialer: false, dialerId: hello.Id!,
            PeerDisplayName(hello.Name, peer), hello.Boot ?? "", remote, hello.Port ?? 0, offset, _opts.Heartbeat);

        if (!TryActivate(session))
        {
            await session.SendAsync(Reject(RejectReason.Duplicate)).ConfigureAwait(false);
            session.Close("conexión duplicada");
            return true;
        }
        if (!await session.SendAsync(new Packet { T = PacketType.Ok, Name = _localName }).ConfigureAwait(false))
            return true; // SendAsync ya cerró la sesión
        session.Start();
        MarkReady(session);
        return true;
    }

    // ------------------------------------------------------------------ marcado (salientes)

    private enum DialResult { Connected, Unreachable, Refused, RejectedUnknown, Rejected, AuthFailed, WrongInstance, Duplicate, Error }

    private async Task DialLoopAsync(CancellationToken ct)
    {
        var backoff = new Backoff(_opts.BackoffSteps);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PeerSettings? peer;
                byte[]? key;
                bool hasSession;
                lock (_gate)
                {
                    peer = _peer?.Clone();
                    key = _peerKey;
                    hasSession = _session != null;
                }

                if (peer == null || key == null || hasSession)
                {
                    backoff.Reset();
                    await _kick.WaitAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    continue;
                }

                lock (_gate) _dialing = true;
                PublishState();
                var connected = await TryConnectOnceAsync(peer, key, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _dialing = false;
                    _dialDetail = null;
                    if (!connected && _session == null) _consecutiveFailures++;
                    hasSession = _session != null;
                }
                PublishState();

                if (connected || hasSession)
                {
                    backoff.Reset();
                    continue;
                }

                var delay = backoff.Next();
                if (backoff.Failures == 1 || backoff.Failures % 10 == 0)
                    Log.Info("net", $"No se pudo conectar con {peer.Name} (intento {backoff.Failures}); próximo intento en {delay.TotalSeconds:0} s");
                if (await _kick.WaitAsync(delay, ct).ConfigureAwait(false))
                {
                    // Despertado por un evento (anuncio, cambio de red): pequeña pausa para agrupar eventos.
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("net", "Error en el bucle de conexión", ex);
                lock (_gate) _dialing = false;
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<bool> TryConnectOnceAsync(PeerSettings peer, byte[] key, CancellationToken ct)
    {
        var candidates = new List<IPEndPoint>();
        void Add(IPEndPoint ep)
        {
            if (!candidates.Contains(ep)) candidates.Add(ep);
        }

        IPEndPoint? hint;
        lock (_gate) hint = _hint;
        if (hint != null) Add(hint);
        var defaultPort = peer.LastPort is > 0 and <= 65535 ? peer.LastPort : AppSettings.DefaultPort;
        if (peer.LastAddress != null && IPAddress.TryParse(peer.LastAddress, out var last))
            Add(new IPEndPoint(last, defaultPort));
        if (peer.ManualAddress != null && NetworkInfo.TryParseHostPort(peer.ManualAddress, defaultPort, out var host, out var mport))
        {
            foreach (var ip in await NetworkInfo.ResolveIPv4Async(host, ct).ConfigureAwait(false))
                Add(new IPEndPoint(ip, mport));
        }

        var tried = new Dictionary<IPEndPoint, DialResult>();
        foreach (var ep in candidates)
        {
            var r = await DialAsync(ep, peer, key, ct).ConfigureAwait(false);
            if (r == DialResult.Connected) return true;
            tried[ep] = r;
            if (IsSessionActive()) return true;
        }

        if (_discovery == null) return false;

        lock (_gate) _dialDetail = "Buscando en la red…";
        PublishState();
        var found = await _discovery.QueryAsync(_opts.DiscoveryTimeout, peer.InstanceId, ct).ConfigureAwait(false);
        var match = found.FirstOrDefault(f => f.InstanceId == peer.InstanceId);
        if (match == null)
        {
            lock (_gate) _firewallSuspect = false;
            return false;
        }

        lock (_gate) _hint = match.EndPoint;
        if (IsSessionActive()) return true;
        var result = tried.TryGetValue(match.EndPoint, out var previous)
            ? previous
            : await DialAsync(match.EndPoint, peer, key, ct).ConfigureAwait(false);
        if (result == DialResult.Connected) return true;

        // Responde por UDP pero no se puede abrir la conexión TCP: casi siempre es el firewall.
        var suspect = result is DialResult.Unreachable or DialResult.Refused;
        bool changed;
        lock (_gate)
        {
            changed = _firewallSuspect != suspect;
            _firewallSuspect = suspect;
        }
        if (suspect && changed)
            Log.Warn("net", $"{peer.Name} responde en {match.Address} pero el puerto TCP {match.Port} no es accesible (¿firewall?)");
        return false;
    }

    private bool IsSessionActive()
    {
        lock (_gate) return _session is { IsClosed: false };
    }

    private async Task<DialResult> DialAsync(IPEndPoint ep, PeerSettings peer, byte[] key, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var handedOff = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opts.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(ep, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return DialResult.Unreachable;
            }
            catch (SocketException se)
            {
                return se.SocketErrorCode == SocketError.ConnectionRefused ? DialResult.Refused : DialResult.Unreachable;
            }

            cts.CancelAfter(_opts.HandshakeTimeout);
            var stream = new NetworkStream(socket, ownsSocket: false);
            var nonceD = HandshakeCrypto.NewNonce();
            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Hello,
                V = ProtocolConstants.Version,
                Mode = HelloMode.Session,
                Id = _opts.InstanceId,
                Name = _localName,
                Boot = BootId,
                Nonce = nonceD,
                Port = _opts.Port,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, cts.Token).ConfigureAwait(false);

            var welcome = await ReadPlainAsync(stream, cts.Token).ConfigureAwait(false);
            if (welcome.T == PacketType.Reject)
            {
                if (welcome.Reason == RejectReason.Unknown)
                {
                    bool firstTime;
                    lock (_gate)
                    {
                        firstTime = !_peerRejectedUs;
                        _peerRejectedUs = true;
                    }
                    if (firstTime) Log.Warn("pairing", $"{peer.Name} ({ep.Address}) no reconoce este vínculo: hay que volver a vincular");
                    return DialResult.RejectedUnknown;
                }
                return DialResult.Rejected;
            }
            if (welcome.T != PacketType.Welcome || welcome.Id != peer.InstanceId)
                return DialResult.WrongInstance; // en esa IP ahora hay otra cosa
            if (welcome.V != ProtocolConstants.Version || welcome.Nonce is not { Length: HandshakeCrypto.NonceSize })
                return DialResult.Rejected;

            var expected = HandshakeCrypto.SessionProof(key, 'L', _opts.InstanceId, peer.InstanceId, nonceD, welcome.Nonce);
            if (!HandshakeCrypto.FixedTimeEquals(expected, welcome.Proof))
            {
                Log.Warn("net", $"{ep.Address} dice ser {peer.Name} pero no demuestra la clave de vínculo; se ignora");
                return DialResult.AuthFailed;
            }

            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Auth,
                Proof = HandshakeCrypto.SessionProof(key, 'D', _opts.InstanceId, peer.InstanceId, nonceD, welcome.Nonce),
            }, cts.Token).ConfigureAwait(false);

            var channel = SecureChannel.Create(key, isDialer: true, nonceD, welcome.Nonce);
            var frame = await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxSessionFrame, cts.Token).ConfigureAwait(false);
            Packet first;
            try
            {
                if (frame == null) throw new CryptographicException("cerrada");
                first = SusurroJson.Deserialize(channel.Open(frame));
            }
            catch (Exception ex) when (ex is CryptographicException or ProtocolException)
            {
                channel.Dispose();
                Log.Warn("net", $"{peer.Name} rechazó la autenticación");
                return DialResult.AuthFailed;
            }
            if (first.T != PacketType.Ok)
            {
                channel.Dispose();
                return first.T == PacketType.Reject && first.Reason == RejectReason.Duplicate ? DialResult.Duplicate : DialResult.Rejected;
            }

            var offset = welcome.Ts is long ts ? ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
            var session = new PeerSession(socket, stream, channel, isDialer: true, dialerId: _opts.InstanceId,
                PeerDisplayName(welcome.Name, peer), welcome.Boot ?? "", ep, welcome.Port ?? ep.Port, offset, _opts.Heartbeat);
            handedOff = true;
            if (!TryActivate(session))
            {
                session.Close("conexión duplicada");
                return DialResult.Duplicate;
            }
            session.Start();
            MarkReady(session);
            return DialResult.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DialResult.Unreachable;
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            return DialResult.Error;
        }
        finally
        {
            if (!handedOff)
            {
                try { socket.Dispose(); } catch { }
            }
        }
    }

    private static string PeerDisplayName(string? announced, PeerSettings peer)
    {
        var n = SettingsValidator.CleanName(announced);
        return n.Length > 0 ? n : peer.Name;
    }

    // ------------------------------------------------------------------ vinculación

    /// <summary>Genera un código y acepta una vinculación entrante durante 5 minutos.</summary>
    public PairingInvitation OpenPairing(TimeSpan? lifetime = null)
    {
        var inv = PairingInvitation.Create(lifetime);
        lock (_gate) _invitation = inv;
        Log.Info("pairing", "Código de vinculación generado (válido 5 minutos)");
        _discovery?.Announce();
        PublishState();
        return inv;
    }

    public void CancelPairing()
    {
        lock (_gate) _invitation = null;
        PublishState();
    }

    private async Task HostPairingAsync(NetworkStream stream, Packet hello, IPEndPoint remote, CancellationToken ct)
    {
        PairingInvitation? inv;
        lock (_gate) inv = _invitation;
        if (inv == null || !inv.IsValid(DateTime.UtcNow))
        {
            await SendPlainAsync(stream, Reject(RejectReason.NotPairing), ct).ConfigureAwait(false);
            return;
        }
        if (!await _pairLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await SendPlainAsync(stream, Reject(RejectReason.Busy), ct).ConfigureAwait(false);
            return;
        }
        try
        {
            if (hello.Key is not { Length: > 0 and < 512 }) throw new ProtocolException("Clave pública inválida");
            using var kx = new PairingKeyExchange();
            var nonceH = HandshakeCrypto.NewNonce();
            var hostKey = kx.PublicKey;
            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Welcome,
                V = ProtocolConstants.Version,
                Mode = HelloMode.Pair,
                Id = _opts.InstanceId,
                Name = _localName,
                Key = hostKey,
                Nonce = nonceH,
                Port = _opts.Port,
            }, ct).ConfigureAwait(false);

            var confirm = await ReadPlainAsync(stream, ct).ConfigureAwait(false);
            if (confirm.T != PacketType.PairConfirm) throw new ProtocolException("Se esperaba pairConfirm");

            byte[] shared;
            try { shared = kx.DeriveShared(hello.Key); }
            catch (CryptographicException) { throw new ProtocolException("Clave pública inválida"); }

            var transcript = PairingCrypto.Transcript(hello.Id!, _opts.InstanceId, hello.Key, hostKey, hello.Nonce!, nonceH);
            var confirmKey = PairingCrypto.ConfirmKey(inv.Code, transcript);
            if (!HandshakeCrypto.FixedTimeEquals(PairingCrypto.ConfirmProof(confirmKey, 'J', transcript, shared), confirm.Proof))
            {
                var exhausted = inv.RegisterFailure();
                Log.Warn("pairing", $"Código incorrecto desde {remote.Address} (intento {inv.Failures}/{PairingInvitation.MaxFailures})");
                if (exhausted)
                {
                    lock (_gate) if (ReferenceEquals(_invitation, inv)) _invitation = null;
                    Log.Warn("pairing", "Código invalidado por demasiados intentos fallidos");
                    InvitationClosed?.Invoke();
                    PublishState();
                }
                await SendPlainAsync(stream, Reject(exhausted ? RejectReason.TooManyAttempts : RejectReason.BadCode), ct).ConfigureAwait(false);
                return;
            }

            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.PairConfirm,
                Proof = PairingCrypto.ConfirmProof(confirmKey, 'H', transcript, shared),
            }, ct).ConfigureAwait(false);

            var pairKey = PairingCrypto.PairKey(shared, transcript, inv.Code);
            CryptographicOperations.ZeroMemory(shared);
            var newPeer = new PeerSettings
            {
                InstanceId = hello.Id!,
                Name = PeerDisplayName(hello.Name, new PeerSettings { Name = "PC remota" }),
                LastAddress = remote.Address.ToString(),
                LastPort = hello.Port is > 0 and <= 65535 ? hello.Port.Value : 0,
                ProtectedKey = _protector.Protect(pairKey),
                PairedUtc = DateTime.UtcNow,
            };
            lock (_gate) if (ReferenceEquals(_invitation, inv)) _invitation = null;
            CompletePairing(newPeer, pairKey);
            InvitationClosed?.Invoke();

            // Esperar a que la otra PC lea la confirmación y cierre (evita un RST que la descarte).
            try
            {
                using var linger = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linger.CancelAfter(2000);
                await stream.ReadAsync(new byte[1], linger.Token).ConfigureAwait(false);
            }
            catch { }
        }
        finally
        {
            _pairLock.Release();
        }
    }

    /// <summary>
    /// Vincula con la PC que está mostrando un código.
    /// <paramref name="address"/>: IP, IP:puerto o nombre de equipo.
    /// </summary>
    public async Task<PairingResult> JoinAsync(string address, string code, bool rememberAddress, CancellationToken ct)
    {
        var normalized = PairingCode.Normalize(code);
        if (normalized == null) return PairingResult.Fail("El código debe tener 8 caracteres (por ejemplo K7QM-4XPD).");
        if (!NetworkInfo.TryParseHostPort(address, AppSettings.DefaultPort, out var host, out var port))
            return PairingResult.Fail("Dirección inválida. Usá una IP (192.168.1.20), IP:puerto o el nombre del equipo.");

        IPAddress[] ips;
        try { ips = await NetworkInfo.ResolveIPv4Async(host, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { ips = Array.Empty<IPAddress>(); }
        if (ips.Length == 0) return PairingResult.Fail($"No se encontró el equipo «{host}» en la red.");

        var last = PairingResult.Fail("No se pudo conectar.");
        foreach (var ip in ips)
        {
            var (result, connected) = await JoinEndpointAsync(new IPEndPoint(ip, port), normalized, rememberAddress ? address.Trim() : null, ct).ConfigureAwait(false);
            if (result.Success || connected) return result;
            last = result;
        }
        return last;
    }

    /// <returns>El resultado y si se llegó a establecer la conexión TCP (para probar la siguiente IP si no).</returns>
    private async Task<(PairingResult Result, bool Connected)> JoinEndpointAsync(IPEndPoint ep, string code, string? manualAddress, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(ep, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (PairingResult.Fail($"No se pudo conectar con {ep}. Verificá que Susurro esté abierto en la otra PC y que el firewall permita el puerto {ep.Port}."), false);
        }
        return (await PairOverSocketAsync(socket, ep, code, manualAddress, cts, ct).ConfigureAwait(false), true);
    }

    private async Task<PairingResult> PairOverSocketAsync(Socket socket, IPEndPoint ep, string code, string? manualAddress, CancellationTokenSource cts, CancellationToken ct)
    {
        try
        {
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var kx = new PairingKeyExchange();
            var nonceJ = HandshakeCrypto.NewNonce();
            var joinerKey = kx.PublicKey;
            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Hello,
                V = ProtocolConstants.Version,
                Mode = HelloMode.Pair,
                Id = _opts.InstanceId,
                Name = _localName,
                Key = joinerKey,
                Nonce = nonceJ,
                Port = _opts.Port,
            }, cts.Token).ConfigureAwait(false);

            var welcome = await ReadPlainAsync(stream, cts.Token).ConfigureAwait(false);
            if (welcome.T == PacketType.Reject) return PairingResult.Fail(DescribeReject(welcome.Reason));
            if (welcome.T != PacketType.Welcome || welcome.Mode != HelloMode.Pair || !SettingsValidator.IsValidInstanceId(welcome.Id)
                || welcome.Key is not { Length: > 0 and < 512 } || welcome.Nonce is not { Length: HandshakeCrypto.NonceSize })
                return PairingResult.Fail("Respuesta inesperada de la otra PC.");
            if (welcome.Id == _opts.InstanceId) return PairingResult.Fail(DescribeReject(RejectReason.Self));

            byte[] shared;
            try { shared = kx.DeriveShared(welcome.Key); }
            catch (CryptographicException) { return PairingResult.Fail("Respuesta inválida de la otra PC."); }

            var transcript = PairingCrypto.Transcript(_opts.InstanceId, welcome.Id!, joinerKey, welcome.Key, nonceJ, welcome.Nonce);
            var confirmKey = PairingCrypto.ConfirmKey(code, transcript);
            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.PairConfirm,
                Proof = PairingCrypto.ConfirmProof(confirmKey, 'J', transcript, shared),
            }, cts.Token).ConfigureAwait(false);

            var response = await ReadPlainAsync(stream, cts.Token).ConfigureAwait(false);
            if (response.T == PacketType.Reject) return PairingResult.Fail(DescribeReject(response.Reason));
            if (response.T != PacketType.PairConfirm ||
                !HandshakeCrypto.FixedTimeEquals(PairingCrypto.ConfirmProof(confirmKey, 'H', transcript, shared), response.Proof))
            {
                Log.Warn("pairing", $"La confirmación de {ep.Address} no es válida (posible interferencia)");
                return PairingResult.Fail("La verificación falló (posible interferencia en la red). Generá un código nuevo y probá otra vez.");
            }

            var pairKey = PairingCrypto.PairKey(shared, transcript, code);
            CryptographicOperations.ZeroMemory(shared);
            var newPeer = new PeerSettings
            {
                InstanceId = welcome.Id!,
                Name = PeerDisplayName(welcome.Name, new PeerSettings { Name = "PC remota" }),
                LastAddress = ep.Address.ToString(),
                LastPort = welcome.Port is > 0 and <= 65535 ? welcome.Port.Value : ep.Port,
                ManualAddress = manualAddress,
                ProtectedKey = _protector.Protect(pairKey),
                PairedUtc = DateTime.UtcNow,
            };
            CompletePairing(newPeer, pairKey);
            return new PairingResult(true, null, newPeer.Clone());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PairingResult.Fail("La otra PC no respondió a tiempo.");
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or EndOfStreamException)
        {
            return PairingResult.Fail("Se perdió la conexión durante la vinculación.");
        }
    }

    private void CompletePairing(PeerSettings peer, byte[] key)
    {
        Log.Info("pairing", $"Vinculado con {peer.Name} (id {peer.InstanceId[..8]}…, {peer.LastAddress})");
        ReplacePeer(peer, key);
        PeerChanged?.Invoke(peer.Clone());
    }

    private static string DescribeReject(string? reason) => reason switch
    {
        RejectReason.NotPairing => "La otra PC no está esperando vinculación. Primero tocá «Mostrar código» en la otra PC.",
        RejectReason.BadCode => "Código incorrecto.",
        RejectReason.TooManyAttempts => "Demasiados intentos fallidos. Generá un código nuevo en la otra PC.",
        RejectReason.Busy => "La otra PC está procesando otra vinculación. Probá de nuevo en unos segundos.",
        RejectReason.Self => "Esa dirección corresponde a esta misma instancia de Susurro.",
        RejectReason.Version => "La otra PC tiene una versión incompatible de Susurro.",
        _ => "La otra PC rechazó la vinculación.",
    };

    // ------------------------------------------------------------------ descubrimiento

    public async Task<IReadOnlyList<DiscoveredInstance>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_discovery == null) return Array.Empty<DiscoveredInstance>();
        return await _discovery.QueryAsync(timeout, null, ct).ConfigureAwait(false);
    }

    private DiscoveryPacket DescribeSelf()
    {
        lock (_gate)
        {
            return new DiscoveryPacket
            {
                Id = _opts.InstanceId,
                Name = _localName,
                Port = _opts.Port,
                Pairing = _invitation is { } i && i.IsValid(DateTime.UtcNow),
                Paired = _peer != null,
            };
        }
    }

    private void OnPeerSeen(DiscoveredInstance d)
    {
        PeerSession? active;
        lock (_gate)
        {
            if (_peer == null || d.InstanceId != _peer.InstanceId) return;
            _hint = d.EndPoint;
            active = _session;
            if (active == null) _consecutiveFailures = 0;
        }
        if (active != null)
            active.Probe(); // la otra PC acaba de arrancar o cambió de red: ¿seguimos vivos?
        else
            Kick();
    }

    // ------------------------------------------------------------------ utilidades

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private LinkState ComputeState()
    {
        lock (_gate)
        {
            if (_peer == null)
            {
                var pairing = _invitation is { } i && i.IsValid(DateTime.UtcNow);
                return new LinkState(LinkStatus.NotPaired, null, null, _listenerError ?? (pairing ? "Esperando vinculación…" : null));
            }
            if (_session is { Ready: true, IsClosed: false } s)
                return new LinkState(LinkStatus.Connected, _peer.Name, s.Remote.Address.ToString(), _listenerError);

            var detail = _peerRejectedUs
                ? "La otra PC no reconoce este vínculo. Volvé a vincular."
                : _firewallSuspect
                    ? "La otra PC responde, pero el puerto TCP no es accesible (¿firewall?)."
                    : _listenerError ?? _dialDetail;
            var status = _dialing && _consecutiveFailures < 3 ? LinkStatus.Connecting : LinkStatus.Disconnected;
            return new LinkState(status, _peer.Name, null, detail);
        }
    }

    private void PublishState()
    {
        var state = ComputeState();
        lock (_gate)
        {
            if (state == _lastState) return;
            _lastState = state;
        }
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { Log.Error("net", "Error notificando estado", ex); }
    }

    private static Packet Reject(string reason) => new() { T = PacketType.Reject, Reason = reason };

    private static Task SendPlainAsync(Stream stream, Packet p, CancellationToken ct) =>
        FrameIO.WriteFrameAsync(stream, SusurroJson.Serialize(p), ct);

    private static async Task<Packet> ReadPlainAsync(Stream stream, CancellationToken ct)
    {
        var frame = await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxHandshakeFrame, ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Conexión cerrada durante el saludo");
        return SusurroJson.Deserialize(frame);
    }

    private sealed class OutMsg
    {
        public OutMsg(string id, Packet packet, DateTime createdUtc)
        {
            Id = id;
            Packet = packet;
            CreatedUtc = createdUtc;
        }

        public string Id { get; }
        public Packet Packet { get; }
        public DateTime CreatedUtc { get; }
        /// <summary>Número de sesión donde se escribió (0 = pendiente de envío).</summary>
        public long SentOnSession { get; set; }
    }
}
