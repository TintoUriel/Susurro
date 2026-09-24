using System.Net;
using System.Net.Sockets;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Net;
using Susurro.Core.Protocol;

namespace Susurro.Core.Discovery;

public sealed record DiscoveredInstance(string InstanceId, string Name, IPAddress Address, int Port, bool PairingOpen, bool Paired)
{
    public IPEndPoint EndPoint => new(Address, Port);
}

/// <summary>
/// Descubrimiento en la LAN por UDP (multicast 239.255.77.77 + broadcast dirigido a cada subred).
/// No genera tráfico periódico:
///  - Responde consultas (recepción asíncrona: 0 % CPU en reposo).
///  - Envía un "anuncio" solo al arrancar, al cambiar la red o al volver de suspensión.
///  - Envía consultas solo cuando hace falta encontrar a la otra PC (y con límites de tiempo).
/// La información recibida por UDP NO es confiable: solo aporta direcciones candidatas; la
/// identidad se verifica criptográficamente al conectar por TCP.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.77.77");
    private const int SioUdpConnReset = -1744830452; // evita excepciones por ICMP "port unreachable"

    private readonly string _localId;
    private readonly int _port;
    private readonly Func<DiscoveryPacket> _describeSelf;
    private readonly object _gate = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private long _replyWindowStart;
    private int _repliesInWindow;

    /// <param name="describeSelf">Devuelve id/nombre/puerto/estado actuales para respuestas y anuncios.</param>
    public DiscoveryService(string localId, int port, Func<DiscoveryPacket> describeSelf)
    {
        _localId = localId;
        _port = port;
        _describeSelf = describeSelf;
    }

    /// <summary>Otra instancia se anunció o está buscando (útil para reconectar al instante).</summary>
    public event Action<DiscoveredInstance>? PeerSeen;

    public int Port => _port;
    public bool IsListening { get { lock (_gate) return _socket != null; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.ExclusiveAddressUse = false;
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.EnableBroadcast = true;
                TryDisableConnReset(s);
                s.Bind(new IPEndPoint(IPAddress.Any, _port));
                s.MulticastLoopback = true;

                var joined = 0;
                foreach (var iface in NetworkInfo.GetInterfaces())
                {
                    try
                    {
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, iface.Address));
                        joined++;
                    }
                    catch (SocketException) { }
                }
                if (joined == 0)
                {
                    try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup)); }
                    catch (SocketException) { }
                }

                _socket = s;
                _cts = new CancellationTokenSource();
                _ = ReceiveLoopAsync(s, _cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn("discovery", $"No se pudo abrir el puerto UDP {_port}; el descubrimiento automático no estará disponible", ex);
                StopLocked();
            }
        }
    }

    /// <summary>Reabre el socket (p. ej. tras un cambio de red, para volver a unirse al grupo multicast).</summary>
    public void Restart() => Start();

    /// <summary>Anuncia esta instancia una vez (al arrancar / cambio de red / reanudar).</summary>
    public void Announce()
    {
        var packet = _describeSelf();
        packet.T = DiscoveryPacket.Announce;
        Socket? s;
        lock (_gate) s = _socket;
        if (s == null) return;
        SendToAll(s, SusurroJson.Serialize(packet), _port);
    }

    /// <summary>
    /// Busca instancias en la LAN. Envía hasta 3 consultas (0 s, 0,6 s, 1,5 s) y recoge respuestas
    /// hasta <paramref name="timeout"/>. Si se indica <paramref name="targetId"/>, termina en cuanto lo encuentra.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredInstance>> QueryAsync(TimeSpan timeout, string? targetId, CancellationToken ct)
    {
        var results = new Dictionary<string, DiscoveredInstance>(StringComparer.Ordinal);
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.EnableBroadcast = true;
            TryDisableConnReset(s);
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (Exception ex)
        {
            Log.Warn("discovery", "No se pudo crear el socket de consulta", ex);
            return Array.Empty<DiscoveredInstance>();
        }

        var query = _describeSelf();
        query.T = DiscoveryPacket.Query;
        query.Nonce = Guid.NewGuid().ToString("N")[..12];
        var payload = SusurroJson.Serialize(query);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var sendTask = Task.Run(async () =>
        {
            int[] delays = { 0, 600, 900 };
            foreach (var d in delays)
            {
                if (d > 0) await Task.Delay(d, deadline.Token).ConfigureAwait(false);
                SendToAll(s, payload, _port);
            }
        }, deadline.Token);

        var buffer = new byte[ProtocolConstants.MaxDatagram * 2];
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var r = await s.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token).ConfigureAwait(false);
                var p = SusurroJson.TryDeserializeDiscovery(buffer.AsSpan(0, r.ReceivedBytes));
                if (p == null || p.T != DiscoveryPacket.Response || p.Id == _localId) continue;
                if (!SettingsValidator.IsValidInstanceId(p.Id) || p.Port is < 1 or > 65535) continue;
                var ep = (IPEndPoint)r.RemoteEndPoint;
                results[p.Id] = new DiscoveredInstance(p.Id, SettingsValidator.CleanName(p.Name), ep.Address, p.Port, p.Pairing, p.Paired);
                if (targetId != null && p.Id == targetId) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        deadline.Cancel();
        try { await sendTask.ConfigureAwait(false); } catch { }
        return results.Values.ToList();
    }

    private async Task ReceiveLoopAsync(Socket s, CancellationToken ct)
    {
        var buffer = new byte[ProtocolConstants.MaxDatagram * 2];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await s.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) return;
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            try
            {
                if (r.ReceivedBytes > ProtocolConstants.MaxDatagram) continue;
                var p = SusurroJson.TryDeserializeDiscovery(buffer.AsSpan(0, r.ReceivedBytes));
                if (p == null || p.Id == _localId || !SettingsValidator.IsValidInstanceId(p.Id)) continue;
                var remote = (IPEndPoint)r.RemoteEndPoint;

                if (p.T == DiscoveryPacket.Query)
                {
                    if (!AllowReply()) continue;
                    var reply = _describeSelf();
                    reply.T = DiscoveryPacket.Response;
                    reply.Nonce = p.Nonce;
                    try { s.SendTo(SusurroJson.Serialize(reply), remote); } catch (SocketException) { }
                }

                if ((p.T == DiscoveryPacket.Query || p.T == DiscoveryPacket.Announce) && p.Port is >= 1 and <= 65535)
                {
                    PeerSeen?.Invoke(new DiscoveredInstance(p.Id, SettingsValidator.CleanName(p.Name), remote.Address, p.Port, p.Pairing, p.Paired));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("discovery", "Datagrama ignorado", ex);
            }
        }
    }

    /// <summary>Límite de 20 respuestas por segundo (evita usar esta PC como amplificador).</summary>
    private bool AllowReply()
    {
        var now = Environment.TickCount64;
        if (now - _replyWindowStart > 1000)
        {
            _replyWindowStart = now;
            _repliesInWindow = 0;
        }
        return ++_repliesInWindow <= 20;
    }

    private static void SendToAll(Socket s, byte[] payload, int port)
    {
        var interfaces = NetworkInfo.GetInterfaces();
        var sent = false;
        foreach (var iface in interfaces)
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.Address.GetAddressBytes());
                s.SendTo(payload, new IPEndPoint(MulticastGroup, port));
                sent = true;
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }

            try { s.SendTo(payload, new IPEndPoint(iface.Broadcast, port)); sent = true; }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
        if (!sent)
        {
            // Sin interfaces detectadas: intento genérico (y loopback para pruebas locales).
            try { s.SendTo(payload, new IPEndPoint(MulticastGroup, port)); } catch { }
            try { s.SendTo(payload, new IPEndPoint(IPAddress.Broadcast, port)); } catch { }
        }
    }

    private static void TryDisableConnReset(Socket s)
    {
        try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch { }
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
    }
}
