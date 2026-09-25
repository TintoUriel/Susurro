using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Discovery;
using Susurro.Core.Identity;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;
using Susurro.Core.Protocol;

namespace Susurro.Core.Net;

public enum LinkStatus
{
    /// <summary>Todavía no se encontró a nadie en la red.</summary>
    NoContacts,
    /// <summary>Hay personas conocidas, pero ninguna conectada ahora.</summary>
    NoneOnline,
    /// <summary>Al menos una persona conectada.</summary>
    Online,
}

public sealed record LinkState(LinkStatus Status, int Online, int Known, string? Detail);

public enum ContactStatus { Offline, Connecting, Online }

public sealed record ContactInfo(string Id, string Name, ContactStatus Status, string? Address, string? Detail, bool Blocked);

public sealed record SendResult(bool Accepted, string? MessageId, string? Error);

public sealed record AddContactResult(bool Success, string? Error, ContactInfo? Contact)
{
    public static AddContactResult Fail(string error) => new(false, error, null);
}

public sealed class PeerLinkOptions
{
    public required LocalIdentity Identity { get; init; }
    public required string LocalName { get; init; }
    public int Port { get; init; } = AppSettings.DefaultPort;
    public int DiscoveryPort { get; init; } = AppSettings.DefaultDiscoveryPort;
    public bool EnableDiscovery { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan[]? BackoffSteps { get; init; }
    /// <summary>
    /// Tras perder la conexión con alguien (sin que avisara que se cerraba), se reintenta con espera
    /// creciente durante este tiempo. Pasado ese plazo solo se reintenta ante un evento (anuncio,
    /// cambio de red, mensaje en espera): con personas apagadas no hay tráfico periódico.
    /// </summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromMinutes(15);
    public HeartbeatOptions Heartbeat { get; init; } = HeartbeatOptions.Default;
    /// <summary>Tiempo máximo que un mensaje espera en la bandeja de salida sin conexión.</summary>
    public TimeSpan OutboxTtl { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Mensajes en espera por destinatario.</summary>
    public int OutboxCapacity { get; init; } = 20;
    /// <summary>Mensajes recibidos con más antigüedad que esta (según el reloj del remitente corregido) se descartan.</summary>
    public TimeSpan StaleMessageAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Núcleo de comunicación con las demás PCs de la oficina (P2P, sin servidor):
///  - Escucha TCP para conexiones entrantes y marca hacia las demás cuando hace falta.
///  - Cualquier Susurro de la red se agrega solo como contacto al conectarse: la identidad se
///    verifica con su clave pública (el id es su hash), sin códigos.
///  - Autentica con la clave de enlace (ECDH de las identidades), cifra con AES-GCM y arbitra
///    conexiones duplicadas por cada par de PCs.
///  - Bandeja de salida por destinatario con reintento tras reconexión, confirmaciones y filtro
///    de duplicados.
/// Todos los eventos se disparan desde hilos del pool: la UI debe pasarlos a su dispatcher.
/// </summary>
public sealed class PeerLink : IAsyncDisposable
{
    /// <summary>Saludos entrantes simultáneos (al arrancar, toda la oficina puede conectarse a la vez).</summary>
    private const int MaxPendingHandshakes = 32;
    /// <summary>Conexiones simultáneas con PCs nuevas; el resto espera su turno.</summary>
    private const int MaxConcurrentProbes = 8;
    /// <summary>Límite de PCs nuevas en cola (protege ante datagramas falsos en masa).</summary>
    private const int MaxQueuedProbes = 64;

    private readonly PeerLinkOptions _opts;
    private readonly LocalIdentity _identity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _linkKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _probing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes, MaxConcurrentProbes);
    private readonly Dictionary<string, long> _probeFailed = new(StringComparer.Ordinal);
    private readonly DuplicateFilter _dupes = new(1024);
    private readonly List<OutMsg> _outbox = new();
    private readonly Timer _outboxTimer;
    private readonly Timer _networkTimer;
    private readonly DiscoveryService? _discovery;
    private readonly CancellationTokenSource _cts = new();

    private volatile string _localName;
    private long _seq;
    private int _pendingHandshakes;
    private string? _listenerError;
    private bool _started;
    private bool _stopped;
    private LinkState? _lastState;
    private IReadOnlyList<ContactInfo> _lastContacts = Array.Empty<ContactInfo>();
    private long _lastRejectLog = -1_000_000;

    public PeerLink(PeerLinkOptions options)
    {
        _opts = options;
        _identity = options.Identity;
        _localName = SettingsValidator.CleanName(options.LocalName);
        BootId = HandshakeCrypto.NewBootId();
        _outboxTimer = new Timer(_ => ExpireOutbox(), null, Timeout.Infinite, Timeout.Infinite);
        _networkTimer = new Timer(_ => OnNetworkSettled(), null, Timeout.Infinite, Timeout.Infinite);
        if (options.EnableDiscovery)
        {
            _discovery = new DiscoveryService(_identity.Id, options.DiscoveryPort, DescribeSelf);
            _discovery.PeerSeen += OnPeerSeen;
        }
    }

    // ------------------------------------------------------------------ eventos / estado

    public event Action<LinkState>? StateChanged;
    /// <summary>Cambió la lista de contactos o el estado de alguno: releer <see cref="Contacts"/>.</summary>
    public event Action? ContactsChanged;
    /// <summary>Cambiaron datos que hay que guardar (contacto nuevo, nombre, dirección, bloqueo).</summary>
    public event Action<IReadOnlyList<ContactSettings>>? ContactsSaved;
    public event Action<WhisperMessage>? MessageReceived;
    public event Action<string, DeliveryState>? DeliveryChanged;

    public string InstanceId => _identity.Id;
    public string BootId { get; }
    public int Port => _opts.Port;
    public string LocalName => _localName;

    public LinkState State => ComputeState();

    /// <summary>Contactos: primero los conectados, luego por nombre.</summary>
    public IReadOnlyList<ContactInfo> Contacts
    {
        get { lock (_gate) return SnapshotContactsLocked(); }
    }

    public ContactInfo? FindContact(string id)
    {
        lock (_gate) return _peers.TryGetValue(id, out var p) ? DescribeLocked(p) : null;
    }

    // ------------------------------------------------------------------ ciclo de vida

    /// <summary>Carga los contactos guardados (antes de <see cref="Start"/>).</summary>
    public void SetContacts(IEnumerable<ContactSettings> contacts)
    {
        lock (_gate)
        {
            foreach (var c in SettingsValidator.NormalizeContacts(contacts.Select(c => c.Clone()), _identity.Id))
                if (!_peers.ContainsKey(c.InstanceId)) _peers[c.InstanceId] = new Peer(c);
        }
    }

    public void Start()
    {
        List<Peer> peers;
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true;
            peers = _peers.Values.ToList();
        }
        Log.Info("net", $"Iniciando: puerto TCP {_opts.Port}, descubrimiento UDP {(_discovery != null ? _opts.DiscoveryPort.ToString() : "desactivado")}, {peers.Count} contacto(s)");
        _ = ListenLoopAsync(_cts.Token);
        foreach (var p in peers) EnsureLoop(p);
        foreach (var p in peers) p.Kick(); // un intento con la última dirección conocida
        if (_discovery != null)
        {
            _discovery.Start();
            _discovery.Announce();
            _ = SweepAsync();
        }
        PublishState();
    }

    public async Task StopAsync()
    {
        List<PeerSession> sessions;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            sessions = _peers.Values.Select(p => p.Session).OfType<PeerSession>().ToList();
        }
        await Task.WhenAll(sessions.Select(async s =>
        {
            try { await s.SendByeAsync("cierre").ConfigureAwait(false); } catch { }
        })).ConfigureAwait(false);
        try { _cts.Cancel(); } catch { }
        _discovery?.Dispose();
        _outboxTimer.Dispose();
        _networkTimer.Dispose();
        lock (_gate)
        {
            foreach (var k in _linkKeys.Values) CryptographicOperations.ZeroMemory(k);
            _linkKeys.Clear();
        }
        Log.Info("net", "Comunicación detenida");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public void UpdateLocalName(string name)
    {
        _localName = SettingsValidator.CleanName(name);
        foreach (var s in ReadySessions())
            _ = s.SendAsync(new Packet { T = PacketType.Profile, Name = _localName });
        _discovery?.Announce();
    }

    /// <summary>Cambió la red local (IP, Wi-Fi, cable) o el equipo volvió de suspensión.</summary>
    public void NotifyNetworkChanged()
    {
        try { _networkTimer.Change(_opts.NetworkChangeDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Forzar la búsqueda y un intento de conexión inmediato con todos (botón "Reconectar").</summary>
    public void ReconnectNow()
    {
        List<Peer> peers;
        lock (_gate)
        {
            peers = _peers.Values.Where(p => p.Session == null && !p.Contact.Blocked).ToList();
            foreach (var p in peers) p.Failures = 0;
        }
        foreach (var p in peers) p.Kick();
        _discovery?.Announce();
        _ = SweepAsync();
    }

    private void OnNetworkSettled()
    {
        if (_cts.IsCancellationRequested) return;
        Log.Info("net", "Cambio de red detectado: " + NetworkInfo.DescribeLocalAddresses());
        _discovery?.Restart();
        foreach (var s in ReadySessions()) s.Probe();
        ReconnectNow();
    }

    // ------------------------------------------------------------------ contactos

    public void SetBlocked(string id, bool blocked)
    {
        PeerSession? close = null;
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var p) || p.Contact.Blocked == blocked) return;
            p.Contact.Blocked = blocked;
            if (blocked)
            {
                close = p.Session;
                p.Session = null;
            }
            p.Failures = 0;
            Log.Info("contacts", $"{p.Contact.Name} {(blocked ? "bloqueado" : "desbloqueado")}");
        }
        close?.Close("bloqueado");
        if (blocked) FailOutboxFor(id);
        PersistContacts();
        PublishState();
        if (!blocked) KickPeer(id);
    }

    /// <summary>Quita a alguien de la lista. Si vuelve a aparecer en la red, se agrega de nuevo.</summary>
    public void Forget(string id)
    {
        Peer? removed;
        lock (_gate)
        {
            if (!_peers.Remove(id, out removed)) return;
            removed.Removed = true;
            _probeFailed[id] = Environment.TickCount64; // que el próximo anuncio no lo vuelva a agregar al instante
        }
        Log.Info("contacts", $"{removed.Contact.Name} quitado de la lista");
        removed.Session?.Close("quitado de la lista");
        removed.Kick(); // termina su bucle
        FailOutboxFor(id);
        PersistContacts();
        PublishState();
    }

    /// <summary>
    /// Conecta con una PC por su dirección (cuando el descubrimiento automático no funciona en la red).
    /// <paramref name="address"/>: IP, IP:puerto o nombre de equipo. Se recuerda para reconectar.
    /// </summary>
    public async Task<AddContactResult> AddByAddressAsync(string address, CancellationToken ct)
    {
        if (!NetworkInfo.TryParseHostPort(address, AppSettings.DefaultPort, out var host, out var port))
            return AddContactResult.Fail("Dirección inválida. Usá una IP (192.168.1.20), IP:puerto o el nombre del equipo.");

        IPAddress[] ips;
        try { ips = await NetworkInfo.ResolveIPv4Async(host, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { ips = Array.Empty<IPAddress>(); }
        if (ips.Length == 0) return AddContactResult.Fail($"No se encontró el equipo «{host}» en la red.");

        var last = DialResult.Unreachable;
        foreach (var ip in ips)
        {
            var (result, peerId) = await DialAsync(new IPEndPoint(ip, port), expectedId: null, ct).ConfigureAwait(false);
            if (result is DialResult.Connected or DialResult.Duplicate && peerId != null)
            {
                ContactSettings? changed = null;
                ContactInfo? info;
                lock (_gate)
                {
                    if (_peers.TryGetValue(peerId, out var p))
                    {
                        p.Contact.ManualAddress = address.Trim();
                        changed = p.Contact;
                    }
                    info = p != null ? DescribeLocked(p) : null;
                }
                if (changed != null) PersistContacts();
                if (info != null) return new AddContactResult(true, null, info);
            }
            last = result;
        }
        return AddContactResult.Fail(last switch
        {
            DialResult.Self => "Esa dirección corresponde a este mismo Susurro.",
            DialResult.RejectedUnknown => "Esa PC no acepta conexiones de esta PC.",
            DialResult.Blocked => "Esa persona está bloqueada. Desbloqueala en la lista.",
            DialResult.Version => "La otra PC tiene una versión incompatible de Susurro. Actualizá las dos.",
            DialResult.AuthFailed => "La otra PC no pudo demostrar su identidad.",
            DialResult.Unreachable or DialResult.Refused =>
                $"No se pudo conectar con {address.Trim()}. Verificá que Susurro esté abierto allí y que el firewall permita el puerto {port}.",
            _ => "No se pudo conectar.",
        });
    }

    /// <summary>Busca a todos en la red y conecta con los que no lo estén.</summary>
    public async Task SweepAsync()
    {
        if (_discovery == null) return;
        try
        {
            var found = await _discovery.QueryAsync(TimeSpan.FromSeconds(3), null, _cts.Token).ConfigureAwait(false);
            foreach (var d in found) OnPeerSeen(d);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("discovery", "Error buscando en la red", ex); }
    }

    // ------------------------------------------------------------------ envío de mensajes

    public SendResult Send(string recipientId, string text, bool urgent, bool wantReceipt)
    {
        if (!MessageRules.TryValidate(text, out var clean, out var error))
            return new SendResult(false, null, error);

        OutMsg msg;
        bool connected;
        Peer? peer;
        lock (_gate)
        {
            if (_stopped) return new SendResult(false, null, "Susurro se está cerrando.");
            if (!_peers.TryGetValue(recipientId, out peer)) return new SendResult(false, null, "Esa persona ya no está en la lista.");
            if (peer.Contact.Blocked) return new SendResult(false, null, $"{peer.Contact.Name} está bloqueado.");
            if (_outbox.Count(m => m.RecipientId == recipientId) >= _opts.OutboxCapacity)
                return new SendResult(false, null, "Demasiados mensajes en espera.");
            var id = MessageRules.NewMessageId();
            msg = new OutMsg(id, recipientId, new Packet
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
            connected = peer.Session is { Ready: true, IsClosed: false };
        }

        if (!connected)
        {
            Log.Info("msg", $"Mensaje en espera para {peer.Contact.Name} (sin conexión) [{msg.Id[..8]}]");
            DeliveryChanged?.Invoke(msg.Id, DeliveryState.Queued);
            peer.Kick();
        }
        EnsureOutboxTimer();
        _ = FlushOutboxAsync(peer);
        return new SendResult(true, msg.Id, null);
    }

    /// <summary>El overlay mostró el mensaje: si el remitente lo pidió, se le avisa ("Visto").</summary>
    public void ReportShown(WhisperMessage message)
    {
        if (message.IsTest || !message.WantsReceipt || message.SenderId == null) return;
        var s = ReadySession(message.SenderId);
        if (s != null) _ = s.SendAsync(new Packet { T = PacketType.Ack, MsgId = message.Id, State = AckState.Shown });
    }

    private async Task FlushOutboxAsync(Peer peer)
    {
        try { await peer.FlushLock.WaitAsync(_cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        try
        {
            var s = ReadySession(peer.Contact.InstanceId);
            if (s == null) return;
            List<OutMsg> pending;
            lock (_gate) pending = _outbox.Where(m => m.RecipientId == peer.Contact.InstanceId && m.SentOnSession == 0).ToList();
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
                Log.Info("msg", $"Mensaje enviado a {peer.Contact.Name} [{m.Id[..8]}] ({m.Packet.Text?.Length ?? 0} caracteres{(m.Packet.Urgent == true ? ", urgente" : "")})");
                DeliveryChanged?.Invoke(m.Id, DeliveryState.Sent);
            }
        }
        catch (Exception ex)
        {
            Log.Error("msg", "Error al enviar la bandeja de salida", ex);
        }
        finally
        {
            try { peer.FlushLock.Release(); } catch { }
        }
    }

    private void EnsureOutboxTimer()
    {
        try { _outboxTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }
    }

    private void StopOutboxTimerIfEmpty()
    {
        bool empty;
        lock (_gate) empty = _outbox.Count == 0;
        if (!empty) return;
        try { _outboxTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void ExpireOutbox()
    {
        List<OutMsg> expired;
        lock (_gate)
        {
            var limit = DateTime.UtcNow - _opts.OutboxTtl;
            expired = _outbox.Where(m => m.CreatedUtc < limit).ToList();
            foreach (var m in expired) _outbox.Remove(m);
        }
        StopOutboxTimerIfEmpty();
        foreach (var m in expired)
        {
            Log.Warn("msg", $"Mensaje no entregado: expiró sin conexión [{m.Id[..8]}]");
            DeliveryChanged?.Invoke(m.Id, DeliveryState.Failed);
        }
    }

    private void FailOutboxFor(string recipientId)
    {
        List<OutMsg> failed;
        lock (_gate)
        {
            failed = _outbox.Where(m => m.RecipientId == recipientId).ToList();
            _outbox.RemoveAll(m => m.RecipientId == recipientId);
        }
        StopOutboxTimerIfEmpty();
        foreach (var m in failed) DeliveryChanged?.Invoke(m.Id, DeliveryState.Failed);
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
                HandleAck(session, p);
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
        else if (sender != session.PeerName) UpdatePeerInfo(session, sender);
        var message = new WhisperMessage(id, text, sender, sentAt, p.Urgent == true, p.Seq ?? 0, p.Receipt == true, SenderId: session.PeerId);
        Log.Info("msg", $"Mensaje recibido de {sender} [{id[..8]}] ({text.Length} caracteres{(message.Urgent ? ", urgente" : "")})");
        MessageReceived?.Invoke(message);
    }

    private void HandleAck(PeerSession session, Packet p)
    {
        if (!MessageRules.IsValidMessageId(p.MsgId)) return;
        var id = p.MsgId!;
        if (p.State == AckState.Received)
        {
            bool removed;
            lock (_gate) removed = _outbox.RemoveAll(m => m.Id == id && m.RecipientId == session.PeerId) > 0;
            StopOutboxTimerIfEmpty();
            if (removed)
            {
                Log.Info("msg", $"Mensaje entregado a {session.PeerName} [{id[..8]}]");
                DeliveryChanged?.Invoke(id, DeliveryState.Delivered);
            }
        }
        else if (p.State == AckState.Shown)
        {
            DeliveryChanged?.Invoke(id, DeliveryState.Shown);
        }
    }

    // ------------------------------------------------------------------ gestión de sesiones

    private PeerSession? ReadySession(string peerId)
    {
        lock (_gate) return _peers.TryGetValue(peerId, out var p) && p.Session is { Ready: true, IsClosed: false } s ? s : null;
    }

    private List<PeerSession> ReadySessions()
    {
        lock (_gate) return _peers.Values.Select(p => p.Session).OfType<PeerSession>().Where(s => s is { Ready: true, IsClosed: false }).ToList();
    }

    private enum Activation { Activated, Duplicate, Refused }

    /// <summary>
    /// Registra una sesión ya autenticada. Si es de alguien nuevo, lo agrega a los contactos.
    /// Si ya hay otra sesión con esa persona, decide cuál queda con <see cref="SessionArbiter"/>.
    /// </summary>
    private Activation TryActivate(PeerSession session)
    {
        PeerSession? replaced = null;
        PeerSession? probe = null;
        Peer? created = null;
        lock (_gate)
        {
            if (_stopped) return Activation.Refused;
            if (!_peers.TryGetValue(session.PeerId, out var p))
            {
                MakeRoomLocked();
                p = new Peer(new ContactSettings { InstanceId = session.PeerId, Name = session.PeerName, LastSeenUtc = DateTime.UtcNow });
                _peers[session.PeerId] = p;
                _probeFailed.Remove(session.PeerId);
                created = p;
            }
            if (p.Contact.Blocked) return Activation.Refused;
            if (p.Session != null && !p.Session.IsClosed)
            {
                if (!SessionArbiter.ShouldReplace(p.Session.DialerId, session.DialerId, _identity.Id, session.PeerId))
                    probe = p.Session;
                else
                    replaced = p.Session;
            }
            if (probe == null)
            {
                p.Session = session;
                session.PacketReceived += OnSessionPacket;
                session.Closed += OnSessionClosed;
            }
        }
        if (created != null)
        {
            Log.Info("contacts", $"Contacto nuevo: {session.PeerName} (id {session.PeerId[..8]}…, {session.Remote.Address})");
            EnsureLoop(created);
        }
        if (probe != null)
        {
            // Nos quedamos con la conexión preferida, pero comprobamos que siga viva.
            probe.Probe();
            return Activation.Duplicate;
        }
        replaced?.Close("reemplazada por una conexión más reciente");
        if (replaced != null) RequeueSentOn(replaced);
        return Activation.Activated;
    }

    /// <summary>Con la lista llena, olvida al contacto desconectado visto hace más tiempo (nunca a un bloqueado).</summary>
    private void MakeRoomLocked()
    {
        if (_peers.Count < SettingsValidator.MaxContacts) return;
        var victim = _peers.Values
            .Where(p => p.Session == null && !p.Contact.Blocked)
            .OrderBy(p => p.Contact.LastSeenUtc)
            .FirstOrDefault();
        if (victim == null) return;
        _peers.Remove(victim.Contact.InstanceId);
        victim.Removed = true;
        victim.Kick();
    }

    private void MarkReady(PeerSession session)
    {
        session.Ready = true;
        lock (_gate)
        {
            if (_peers.TryGetValue(session.PeerId, out var p) && ReferenceEquals(p.Session, session))
            {
                p.Failures = 0;
                p.RejectedUs = false;
                p.FirewallSuspect = false;
                p.DialDetail = null;
                p.LastOnlineTick = Environment.TickCount64;
                p.Contact.LastSeenUtc = DateTime.UtcNow;
            }
        }
        Log.Info("net", $"Conectado con {session.PeerName} ({session.Remote.Address}, {(session.IsDialer ? "saliente" : "entrante")})");
        UpdatePeerInfo(session, session.PeerName, forcePersist: true);
        PublishState();
        if (TryGetPeer(session.PeerId) is { } peer) _ = FlushOutboxAsync(peer);
    }

    private void OnSessionClosed(PeerSession session, string reason)
    {
        bool wasActive;
        Peer? peer;
        lock (_gate)
        {
            wasActive = _peers.TryGetValue(session.PeerId, out peer) && ReferenceEquals(peer.Session, session);
            if (wasActive)
            {
                peer!.Session = null;
                // Si avisó que se cerraba, volverá a anunciarse al arrancar: no hace falta insistir.
                if (session.ClosedByPeer) peer.LastOnlineTick = 0;
                else if (session.Ready) peer.LastOnlineTick = Environment.TickCount64;
            }
        }
        session.PacketReceived -= OnSessionPacket;
        session.Closed -= OnSessionClosed;
        RequeueSentOn(session);
        if (!wasActive) return;
        if (session.Ready) Log.Info("net", $"Desconectado de {session.PeerName}: {reason}");
        PublishState();
        if (!_cts.IsCancellationRequested) peer!.Kick();
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
        if (requeued.Count > 0 && TryGetPeer(session.PeerId) is { } p)
        {
            p.Kick();
            _ = FlushOutboxAsync(p);
        }
    }

    private void UpdatePeerInfo(PeerSession session, string peerName, bool forcePersist = false)
    {
        var changed = forcePersist;
        lock (_gate)
        {
            if (!_peers.TryGetValue(session.PeerId, out var p) || !ReferenceEquals(p.Session, session)) return;
            var c = p.Contact;
            var address = session.Remote.Address.ToString();
            var port = session.IsDialer ? session.Remote.Port : session.PeerPort;
            if (peerName.Length > 0 && c.Name != peerName) { c.Name = peerName; changed = true; }
            if (c.LastAddress != address) { c.LastAddress = address; changed = true; }
            if (port is > 0 and <= 65535 && c.LastPort != port) { c.LastPort = port; changed = true; }
            session.PeerName = c.Name;
        }
        if (!changed) return;
        PersistContacts();
        PublishState();
    }

    private Peer? TryGetPeer(string id)
    {
        lock (_gate) return _peers.TryGetValue(id, out var p) ? p : null;
    }

    private void KickPeer(string id) => TryGetPeer(id)?.Kick();

    private void PersistContacts()
    {
        List<ContactSettings> copy;
        lock (_gate) copy = _peers.Values.Select(p => p.Contact.Clone()).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        try { ContactsSaved?.Invoke(copy); }
        catch (Exception ex) { Log.Error("contacts", "Error guardando contactos", ex); }
    }

    // ------------------------------------------------------------------ clave de enlace

    /// <summary>Clave de enlace con otra instancia (se calcula una vez y queda en memoria).</summary>
    private byte[] LinkKeyFor(string peerId, byte[] peerPublicKey)
    {
        lock (_gate)
        {
            if (_linkKeys.TryGetValue(peerId, out var cached)) return cached;
        }
        var key = _identity.DeriveLinkKey(peerId, peerPublicKey); // lanza CryptographicException si la clave es inválida
        lock (_gate)
        {
            if (_linkKeys.TryGetValue(peerId, out var cached))
            {
                CryptographicOperations.ZeroMemory(key);
                return cached;
            }
            if (_linkKeys.Count >= SettingsValidator.MaxContacts * 2)
            {
                // Límite de memoria ante identidades falsas en masa: se descartan las que no son contactos.
                foreach (var stale in _linkKeys.Keys.Where(k => !_peers.ContainsKey(k)).ToList())
                {
                    CryptographicOperations.ZeroMemory(_linkKeys[stale]);
                    _linkKeys.Remove(stale);
                }
            }
            _linkKeys[peerId] = key;
            return key;
        }
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
                listener.Start(16);
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
                LogRejected($"Conexión de {remote.Address} con versión de protocolo incompatible ({hello.V})");
                return;
            }
            if (hello.Mode != HelloMode.Session) throw new ProtocolException("Modo desconocido");
            if (!SettingsValidator.IsValidInstanceId(hello.Id) || hello.Nonce is not { Length: HandshakeCrypto.NonceSize })
                throw new ProtocolException("hello inválido");
            if (hello.Id == _identity.Id)
            {
                await SendPlainAsync(stream, Reject(RejectReason.Self), hs.Token).ConfigureAwait(false);
                return;
            }

            handedOff = await AcceptSessionAsync(socket, stream, hello, remote, hs.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) Log.Warn("net", $"Tiempo de espera agotado en el saludo de {remote?.Address}");
        }
        catch (ProtocolException ex)
        {
            LogRejected($"Conexión inválida de {remote?.Address}: {ex.Message}");
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
        var peerId = hello.Id!;
        // El id es el hash de la clave pública: nadie puede presentarse con el id de otro.
        if (!LocalIdentity.Matches(peerId, hello.Key))
        {
            LogRejected($"Conexión rechazada de {remote.Address}: la clave no corresponde al id");
            await SendPlainAsync(stream, Reject(RejectReason.Auth), ct).ConfigureAwait(false);
            return false;
        }
        if (IsBlocked(peerId))
        {
            LogRejected($"Conexión rechazada de {remote.Address}: persona bloqueada");
            await SendPlainAsync(stream, Reject(RejectReason.Unknown), ct).ConfigureAwait(false);
            return false;
        }

        byte[] key;
        try { key = LinkKeyFor(peerId, hello.Key!); }
        catch (CryptographicException)
        {
            await SendPlainAsync(stream, Reject(RejectReason.Auth), ct).ConfigureAwait(false);
            return false;
        }

        var nonceD = hello.Nonce!;
        var nonceL = HandshakeCrypto.NewNonce();
        await SendPlainAsync(stream, new Packet
        {
            T = PacketType.Welcome,
            V = ProtocolConstants.Version,
            Mode = HelloMode.Session,
            Id = _identity.Id,
            Name = _localName,
            Key = _identity.PublicKey,
            Boot = BootId,
            Nonce = nonceL,
            Proof = HandshakeCrypto.SessionProof(key, 'L', peerId, _identity.Id, nonceD, nonceL),
            Port = _opts.Port,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, ct).ConfigureAwait(false);

        var auth = await ReadPlainAsync(stream, ct).ConfigureAwait(false);
        var expected = HandshakeCrypto.SessionProof(key, 'D', peerId, _identity.Id, nonceD, nonceL);
        if (auth.T != PacketType.Auth || !HandshakeCrypto.FixedTimeEquals(expected, auth.Proof))
        {
            LogRejected($"Autenticación fallida desde {remote.Address}");
            await SendPlainAsync(stream, Reject(RejectReason.Auth), ct).ConfigureAwait(false);
            return false;
        }

        var channel = SecureChannel.Create(key, isDialer: false, nonceD, nonceL);
        var offset = hello.Ts is long ts ? ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
        var session = new PeerSession(socket, stream, channel, isDialer: false, dialerId: peerId, peerId,
            DisplayName(hello.Name, peerId), hello.Boot ?? "", remote, hello.Port ?? 0, offset, _opts.Heartbeat);

        switch (TryActivate(session))
        {
            case Activation.Duplicate:
                await session.SendAsync(Reject(RejectReason.Duplicate)).ConfigureAwait(false);
                session.Close("conexión duplicada");
                return true;
            case Activation.Refused:
                await session.SendAsync(Reject(RejectReason.Unknown)).ConfigureAwait(false);
                session.Close("rechazada");
                return true;
        }
        if (!await session.SendAsync(new Packet { T = PacketType.Ok, Name = _localName }).ConfigureAwait(false))
            return true; // SendAsync ya cerró la sesión
        session.Start();
        MarkReady(session);
        return true;
    }

    private bool IsBlocked(string id)
    {
        lock (_gate) return _peers.TryGetValue(id, out var p) && p.Contact.Blocked;
    }

    /// <summary>Registra rechazos como máximo uno cada 5 minutos (evita llenar el log desde la red).</summary>
    private void LogRejected(string message)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastRejectLog) <= 300_000) return;
        Interlocked.Exchange(ref _lastRejectLog, now);
        Log.Warn("net", message);
    }

    // ------------------------------------------------------------------ marcado (salientes)

    private enum DialResult { Connected, Duplicate, Unreachable, Refused, RejectedUnknown, Rejected, Version, AuthFailed, WrongInstance, Blocked, Self, Error }

    private void EnsureLoop(Peer p)
    {
        lock (_gate)
        {
            if (p.LoopStarted || !_started || _stopped) return;
            p.LoopStarted = true;
        }
        _ = PeerLoopAsync(p, _cts.Token);
    }

    /// <summary>
    /// Un bucle por contacto que duerme hasta que haya un motivo para conectar (arranque, anuncio de
    /// la otra PC, cambio de red, mensaje en espera, conexión perdida). Sin motivo no hay tráfico.
    /// </summary>
    private async Task PeerLoopAsync(Peer p, CancellationToken ct)
    {
        var backoff = new Backoff(_opts.BackoffSteps);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await p.KickSignal.WaitAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                backoff.Reset();
                while (!ct.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (p.Removed) return;
                        if (p.Session != null || p.Contact.Blocked) break;
                        p.Dialing = true;
                    }
                    PublishState();
                    var connected = await TryConnectOnceAsync(p, ct).ConfigureAwait(false);
                    bool hasSession;
                    bool retry;
                    lock (_gate)
                    {
                        p.Dialing = false;
                        p.DialDetail = null;
                        hasSession = p.Session != null;
                        if (!connected && !hasSession) p.Failures++;
                        retry = ShouldRetryLocked(p);
                    }
                    PublishState();
                    if (connected || hasSession || !retry) break;

                    var delay = backoff.Next();
                    if (backoff.Failures == 1 || backoff.Failures % 10 == 0)
                        Log.Info("net", $"No se pudo conectar con {p.Contact.Name} (intento {backoff.Failures}); próximo intento en {delay.TotalSeconds:0} s");
                    if (await p.KickSignal.WaitAsync(delay, ct).ConfigureAwait(false))
                    {
                        // Despertado por un evento (anuncio, cambio de red): pequeña pausa para agrupar eventos.
                        await Task.Delay(300, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("net", "Error en el bucle de conexión", ex);
                lock (_gate) p.Dialing = false;
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Se sigue reintentando solo si hay mensajes esperando o si la conexión se perdió hace poco.</summary>
    private bool ShouldRetryLocked(Peer p)
    {
        if (p.Removed || p.Contact.Blocked) return false;
        if (_outbox.Any(m => m.RecipientId == p.Contact.InstanceId)) return true;
        return p.LastOnlineTick != 0 && Environment.TickCount64 - p.LastOnlineTick < _opts.RetryWindow.TotalMilliseconds;
    }

    private async Task<bool> TryConnectOnceAsync(Peer p, CancellationToken ct)
    {
        ContactSettings c;
        IPEndPoint? hint;
        bool pending;
        lock (_gate)
        {
            c = p.Contact.Clone();
            hint = p.Hint;
            pending = _outbox.Any(m => m.RecipientId == c.InstanceId);
        }

        var candidates = new List<IPEndPoint>();
        void Add(IPEndPoint ep)
        {
            if (!candidates.Contains(ep)) candidates.Add(ep);
        }
        if (hint != null) Add(hint);
        var defaultPort = c.LastPort is > 0 and <= 65535 ? c.LastPort : AppSettings.DefaultPort;
        if (c.LastAddress != null && IPAddress.TryParse(c.LastAddress, out var last))
            Add(new IPEndPoint(last, defaultPort));
        if (c.ManualAddress != null && NetworkInfo.TryParseHostPort(c.ManualAddress, defaultPort, out var host, out var mport))
        {
            try
            {
                foreach (var ip in await NetworkInfo.ResolveIPv4Async(host, ct).ConfigureAwait(false))
                    Add(new IPEndPoint(ip, mport));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        var tried = new Dictionary<IPEndPoint, DialResult>();
        foreach (var ep in candidates)
        {
            var (r, _) = await DialAsync(ep, c.InstanceId, ct).ConfigureAwait(false);
            if (r is DialResult.Connected or DialResult.Duplicate) return true;
            tried[ep] = r;
            if (HasSession(p)) return true;
        }

        // Buscar por la red solo si hay algo que entregar: el resto se reconecta cuando la otra PC se anuncia.
        if (_discovery == null || !pending) return false;

        lock (_gate) p.DialDetail = "Buscando en la red…";
        PublishState();
        var found = await _discovery.QueryAsync(_opts.DiscoveryTimeout, c.InstanceId, ct).ConfigureAwait(false);
        var match = found.FirstOrDefault(f => f.InstanceId == c.InstanceId);
        if (match == null)
        {
            lock (_gate) p.FirewallSuspect = false;
            return false;
        }

        lock (_gate) p.Hint = match.EndPoint;
        if (HasSession(p)) return true;
        var result = tried.TryGetValue(match.EndPoint, out var previous)
            ? previous
            : (await DialAsync(match.EndPoint, c.InstanceId, ct).ConfigureAwait(false)).Result;
        if (result is DialResult.Connected or DialResult.Duplicate) return true;

        // Responde por UDP pero no se puede abrir la conexión TCP: casi siempre es el firewall.
        var suspect = result is DialResult.Unreachable or DialResult.Refused;
        bool changed;
        lock (_gate)
        {
            changed = p.FirewallSuspect != suspect;
            p.FirewallSuspect = suspect;
        }
        if (suspect && changed)
            Log.Warn("net", $"{c.Name} responde en {match.Address} pero el puerto TCP {match.Port} no es accesible (¿firewall?)");
        return false;
    }

    private bool HasSession(Peer p)
    {
        lock (_gate) return p.Session is { IsClosed: false };
    }

    /// <param name="expectedId">Id de quien se espera encontrar; null = aceptar a cualquiera (dirección manual).</param>
    private async Task<(DialResult Result, string? PeerId)> DialAsync(IPEndPoint ep, string? expectedId, CancellationToken ct)
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
                return (DialResult.Unreachable, null);
            }
            catch (SocketException se)
            {
                return (se.SocketErrorCode == SocketError.ConnectionRefused ? DialResult.Refused : DialResult.Unreachable, null);
            }

            cts.CancelAfter(_opts.HandshakeTimeout);
            var stream = new NetworkStream(socket, ownsSocket: false);
            var nonceD = HandshakeCrypto.NewNonce();
            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Hello,
                V = ProtocolConstants.Version,
                Mode = HelloMode.Session,
                Id = _identity.Id,
                Name = _localName,
                Key = _identity.PublicKey,
                Boot = BootId,
                Nonce = nonceD,
                Port = _opts.Port,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, cts.Token).ConfigureAwait(false);

            var welcome = await ReadPlainAsync(stream, cts.Token).ConfigureAwait(false);
            if (welcome.T == PacketType.Reject)
            {
                switch (welcome.Reason)
                {
                    case RejectReason.Unknown:
                        if (expectedId != null && TryGetPeer(expectedId) is { } rp)
                        {
                            bool firstTime;
                            lock (_gate)
                            {
                                firstTime = !rp.RejectedUs;
                                rp.RejectedUs = true;
                            }
                            if (firstTime) Log.Warn("net", $"{rp.Contact.Name} ({ep.Address}) no acepta conexiones de esta PC");
                        }
                        return (DialResult.RejectedUnknown, null);
                    case RejectReason.Version:
                        return (DialResult.Version, null);
                    case RejectReason.Self:
                        return (DialResult.Self, null);
                    default:
                        return (DialResult.Rejected, null);
                }
            }
            if (welcome.T != PacketType.Welcome || !SettingsValidator.IsValidInstanceId(welcome.Id))
                return (DialResult.Error, null);
            var peerId = welcome.Id!;
            if (peerId == _identity.Id) return (DialResult.Self, null);
            if (expectedId != null && peerId != expectedId)
                return (DialResult.WrongInstance, null); // en esa IP ahora hay otra persona
            if (welcome.V != ProtocolConstants.Version) return (DialResult.Version, null);
            if (welcome.Nonce is not { Length: HandshakeCrypto.NonceSize }) return (DialResult.Error, null);
            if (!LocalIdentity.Matches(peerId, welcome.Key))
            {
                Log.Warn("net", $"{ep.Address} presentó una clave que no corresponde a su id; se ignora");
                return (DialResult.AuthFailed, null);
            }
            if (IsBlocked(peerId)) return (DialResult.Blocked, peerId);

            byte[] key;
            try { key = LinkKeyFor(peerId, welcome.Key!); }
            catch (CryptographicException) { return (DialResult.AuthFailed, null); }

            var expected = HandshakeCrypto.SessionProof(key, 'L', _identity.Id, peerId, nonceD, welcome.Nonce);
            if (!HandshakeCrypto.FixedTimeEquals(expected, welcome.Proof))
            {
                Log.Warn("net", $"{ep.Address} no demostró su identidad; se ignora");
                return (DialResult.AuthFailed, null);
            }

            await SendPlainAsync(stream, new Packet
            {
                T = PacketType.Auth,
                Proof = HandshakeCrypto.SessionProof(key, 'D', _identity.Id, peerId, nonceD, welcome.Nonce),
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
                Log.Warn("net", $"{ep.Address} rechazó la autenticación");
                return (DialResult.AuthFailed, null);
            }
            if (first.T != PacketType.Ok)
            {
                channel.Dispose();
                return first.T == PacketType.Reject && first.Reason == RejectReason.Duplicate
                    ? (DialResult.Duplicate, peerId)
                    : (DialResult.Rejected, null);
            }

            var offset = welcome.Ts is long ts ? ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
            var session = new PeerSession(socket, stream, channel, isDialer: true, dialerId: _identity.Id, peerId,
                DisplayName(welcome.Name, peerId), welcome.Boot ?? "", ep, welcome.Port ?? ep.Port, offset, _opts.Heartbeat);
            handedOff = true;
            switch (TryActivate(session))
            {
                case Activation.Duplicate:
                    session.Close("conexión duplicada");
                    return (DialResult.Duplicate, peerId);
                case Activation.Refused:
                    session.Close("rechazada");
                    return (DialResult.Blocked, peerId);
            }
            session.Start();
            MarkReady(session);
            return (DialResult.Connected, peerId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (DialResult.Unreachable, null);
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            return (DialResult.Error, null);
        }
        finally
        {
            if (!handedOff)
            {
                try { socket.Dispose(); } catch { }
            }
        }
    }

    private string DisplayName(string? announced, string peerId)
    {
        var n = SettingsValidator.CleanName(announced);
        if (n.Length > 0) return n;
        lock (_gate) return _peers.TryGetValue(peerId, out var p) ? p.Contact.Name : "Sin nombre";
    }

    // ------------------------------------------------------------------ descubrimiento

    private DiscoveryPacket DescribeSelf() => new()
    {
        Id = _identity.Id,
        Name = _localName,
        Port = _opts.Port,
    };

    private void OnPeerSeen(DiscoveredInstance d)
    {
        if (d.InstanceId == _identity.Id) return;
        PeerSession? active = null;
        Peer? known;
        lock (_gate)
        {
            if (_stopped) return;
            if (_peers.TryGetValue(d.InstanceId, out known))
            {
                if (known.Contact.Blocked) return;
                known.Hint = d.EndPoint;
                active = known.Session;
                if (active == null) known.Failures = 0;
            }
            else
            {
                // Alguien nuevo en la red. El datagrama no es confiable: se intenta una conexión
                // (que verifica su identidad) con límites para que no se pueda abusar.
                var now = Environment.TickCount64;
                if (_probeFailed.TryGetValue(d.InstanceId, out var failedAt) && now - failedAt < 60_000) return;
                if (_probing.Count >= MaxQueuedProbes || !_probing.Add(d.InstanceId)) return;
            }
        }
        if (known != null)
        {
            if (active != null) active.Probe(); // la otra PC acaba de arrancar o cambió de red: ¿seguimos vivos?
            else known.Kick();
            return;
        }
        _ = ProbeNewAsync(d);
    }

    private async Task ProbeNewAsync(DiscoveredInstance d)
    {
        var ok = false;
        var slot = false;
        try
        {
            await _probeSlots.WaitAsync(_cts.Token).ConfigureAwait(false);
            slot = true;
            // Mientras esperaba su turno, quizás la otra PC ya se conectó sola.
            if (TryGetPeer(d.InstanceId) == null)
            {
                var (result, _) = await DialAsync(d.EndPoint, d.InstanceId, _cts.Token).ConfigureAwait(false);
                ok = result is DialResult.Connected or DialResult.Duplicate;
            }
            else ok = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("net", "Error conectando con una PC nueva", ex); }
        finally
        {
            if (slot) _probeSlots.Release();
            lock (_gate)
            {
                _probing.Remove(d.InstanceId);
                if (!ok)
                {
                    if (_probeFailed.Count > 256) _probeFailed.Clear();
                    _probeFailed[d.InstanceId] = Environment.TickCount64;
                }
            }
        }
    }

    // ------------------------------------------------------------------ estado

    private ContactInfo DescribeLocked(Peer p)
    {
        var c = p.Contact;
        if (p.Session is { Ready: true, IsClosed: false } s)
            return new ContactInfo(c.InstanceId, c.Name, ContactStatus.Online, s.Remote.Address.ToString(), null, c.Blocked);
        var detail = c.Blocked
            ? null
            : p.RejectedUs
                ? "No acepta conexiones de esta PC."
                : p.FirewallSuspect
                    ? "Responde, pero el puerto TCP no es accesible (¿firewall?)."
                    : p.DialDetail;
        var status = p.Dialing && p.Failures < 3 ? ContactStatus.Connecting : ContactStatus.Offline;
        return new ContactInfo(c.InstanceId, c.Name, status, c.LastAddress, detail, c.Blocked);
    }

    private IReadOnlyList<ContactInfo> SnapshotContactsLocked() =>
        _peers.Values.Select(DescribeLocked)
            .OrderBy(c => c.Blocked)
            .ThenByDescending(c => c.Status == ContactStatus.Online)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    private LinkState ComputeState()
    {
        lock (_gate)
        {
            var known = _peers.Values.Count(p => !p.Contact.Blocked);
            var online = _peers.Values.Count(p => !p.Contact.Blocked && p.Session is { Ready: true, IsClosed: false });
            var status = online > 0 ? LinkStatus.Online : known > 0 ? LinkStatus.NoneOnline : LinkStatus.NoContacts;
            return new LinkState(status, online, known, _listenerError);
        }
    }

    private void PublishState()
    {
        var state = ComputeState();
        bool stateChanged;
        bool contactsChanged;
        lock (_gate)
        {
            stateChanged = state != _lastState;
            _lastState = state;
            var contacts = SnapshotContactsLocked();
            contactsChanged = !contacts.SequenceEqual(_lastContacts);
            if (contactsChanged) _lastContacts = contacts;
        }
        try
        {
            if (stateChanged) StateChanged?.Invoke(state);
            if (contactsChanged) ContactsChanged?.Invoke();
        }
        catch (Exception ex) { Log.Error("net", "Error notificando estado", ex); }
    }

    // ------------------------------------------------------------------ utilidades

    private static Packet Reject(string reason) => new() { T = PacketType.Reject, Reason = reason };

    private static Task SendPlainAsync(Stream stream, Packet p, CancellationToken ct) =>
        FrameIO.WriteFrameAsync(stream, SusurroJson.Serialize(p), ct);

    private static async Task<Packet> ReadPlainAsync(Stream stream, CancellationToken ct)
    {
        var frame = await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxHandshakeFrame, ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Conexión cerrada durante el saludo");
        return SusurroJson.Deserialize(frame);
    }

    /// <summary>Estado en memoria de un contacto. Se modifica bajo <c>_gate</c>.</summary>
    private sealed class Peer
    {
        public Peer(ContactSettings contact) => Contact = contact;

        public ContactSettings Contact { get; }
        public PeerSession? Session { get; set; }
        public IPEndPoint? Hint { get; set; }
        public bool Dialing { get; set; }
        public string? DialDetail { get; set; }
        public int Failures { get; set; }
        public bool RejectedUs { get; set; }
        public bool FirewallSuspect { get; set; }
        /// <summary>Última vez (TickCount64) que estuvo conectado en esta ejecución; 0 = no reintentar solo.</summary>
        public long LastOnlineTick { get; set; }
        public bool LoopStarted { get; set; }
        public bool Removed { get; set; }
        public SemaphoreSlim KickSignal { get; } = new(0, 1);
        public SemaphoreSlim FlushLock { get; } = new(1, 1);

        public void Kick()
        {
            try { KickSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class OutMsg
    {
        public OutMsg(string id, string recipientId, Packet packet, DateTime createdUtc)
        {
            Id = id;
            RecipientId = recipientId;
            Packet = packet;
            CreatedUtc = createdUtc;
        }

        public string Id { get; }
        public string RecipientId { get; }
        public Packet Packet { get; }
        public DateTime CreatedUtc { get; }
        /// <summary>Número de sesión donde se escribió (0 = pendiente de envío).</summary>
        public long SentOnSession { get; set; }
    }
}
