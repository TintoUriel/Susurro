using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Discovery;
using Susurro.Core.Messaging;
using Susurro.Core.Net;
using Susurro.Core.Pairing;
using Susurro.Core.Protocol;

namespace Susurro.Core.Tests;

/// <summary>
/// Pruebas de extremo a extremo con sockets TCP reales en 127.0.0.1:
/// dos instancias de <see cref="PeerLink"/> se vinculan, conectan, intercambian mensajes y se reconectan.
/// </summary>
public class PeerLinkIntegrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    internal sealed class Node : IAsyncDisposable
    {
        public readonly ConcurrentQueue<WhisperMessage> Received = new();
        public readonly ConcurrentDictionary<string, DeliveryState> Delivery = new();
        public volatile PeerSettings? LastPeer;

        private Node(string id, int port, PeerSettings? peer)
        {
            Id = id;
            Port = port;
            Link = new PeerLink(new PeerLinkOptions
            {
                InstanceId = id,
                LocalName = "N" + id[..4],
                Port = port,
                EnableDiscovery = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                BackoffSteps = new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400) },
                OutboxTtl = TimeSpan.FromSeconds(30),
            }, new PlainKeyProtector());
            Link.MessageReceived += m => Received.Enqueue(m);
            Link.DeliveryChanged += (id2, st) => Delivery.AddOrUpdate(id2, st, (_, old) => st > old || st == DeliveryState.Queued ? st : old);
            Link.PeerChanged += p => LastPeer = p;
            if (peer != null) Link.SetPeer(peer);
            Link.Start();
        }

        public string Id { get; }
        public int Port { get; }
        public PeerLink Link { get; }

        public static Node Create(string? id = null, int? port = null, PeerSettings? peer = null) =>
            new(id ?? SettingsValidator.NewInstanceId(), port ?? FreePort(), peer);

        public ValueTask DisposeAsync() => Link.DisposeAsync();
    }

    internal static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal static async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Wait);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Tiempo agotado esperando: " + what);
            await Task.Delay(25);
        }
    }

    private static async Task<(Node A, Node B)> PairAsync()
    {
        var a = Node.Create();
        var b = Node.Create();
        var inv = a.Link.OpenPairing();
        var result = await b.Link.JoinAsync($"127.0.0.1:{a.Port}", PairingCode.Format(inv.Code), rememberAddress: false, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        await WaitUntil(() => a.Link.State.Status == LinkStatus.Connected && b.Link.State.Status == LinkStatus.Connected, "conexión tras vincular");
        return (a, b);
    }

    [Fact]
    public async Task Pair_connect_and_exchange_messages_with_receipts()
    {
        var (a, b) = await PairAsync();
        await using var _a = a;
        await using var _b = b;

        Assert.Equal(b.Id, a.Link.Peer!.InstanceId);
        Assert.Equal(a.Id, b.Link.Peer!.InstanceId);
        Assert.NotNull(a.LastPeer);
        Assert.NotNull(b.LastPeer);
        Assert.Null(a.Link.ActiveInvitation); // el código se consume al vincular

        var send = a.Link.Send("  ¿Podés venir?  ", urgent: true, wantReceipt: true);
        Assert.True(send.Accepted);
        await WaitUntil(() => b.Received.Count == 1, "recepción");
        Assert.True(b.Received.TryPeek(out var msg));
        Assert.Equal("¿Podés venir?", msg!.Text);
        Assert.True(msg.Urgent);
        Assert.True(msg.WantsReceipt);
        Assert.Equal(a.Link.LocalName, msg.SenderName);

        await WaitUntil(() => a.Delivery.TryGetValue(send.MessageId!, out var st) && st == DeliveryState.Delivered, "confirmación de entrega");
        b.Link.ReportShown(msg);
        await WaitUntil(() => a.Delivery[send.MessageId!] == DeliveryState.Shown, "confirmación de mostrado");

        // Y en sentido inverso.
        var back = b.Link.Send("Voy", false, false);
        await WaitUntil(() => a.Received.Count == 1, "respuesta");
        Assert.Equal("Voy", a.Received.First().Text);
        await WaitUntil(() => b.Delivery[back.MessageId!] == DeliveryState.Delivered, "entrega de la respuesta");
    }

    [Fact]
    public async Task Invalid_messages_are_rejected_locally()
    {
        await using var a = Node.Create();
        Assert.False(a.Link.Send("hola", false, false).Accepted); // sin vincular
        var (x, y) = await PairAsync();
        await using var _x = x;
        await using var _y = y;
        Assert.False(x.Link.Send("   ", false, false).Accepted);
        Assert.False(x.Link.Send(new string('a', MessageRules.MaxLength + 1), false, false).Accepted);
    }

    [Fact]
    public async Task Wrong_code_fails_and_correct_code_still_works()
    {
        await using var a = Node.Create();
        await using var b = Node.Create();
        var inv = a.Link.OpenPairing();
        var wrong = inv.Code == "00000000" ? "11111111" : "00000000";

        var bad = await b.Link.JoinAsync($"127.0.0.1:{a.Port}", wrong, false, CancellationToken.None);
        Assert.False(bad.Success);
        Assert.Contains("incorrecto", bad.Error);
        Assert.Null(b.Link.Peer);
        Assert.Null(a.Link.Peer);

        var good = await b.Link.JoinAsync($"127.0.0.1:{a.Port}", inv.Code, false, CancellationToken.None);
        Assert.True(good.Success, good.Error);
    }

    [Fact]
    public async Task Pairing_without_active_code_is_refused()
    {
        await using var a = Node.Create();
        await using var b = Node.Create();
        var r = await b.Link.JoinAsync($"127.0.0.1:{a.Port}", "K7QM4XPD", false, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("no está esperando", r.Error);
    }

    [Fact]
    public async Task Code_is_invalidated_after_too_many_failures()
    {
        await using var a = Node.Create();
        await using var b = Node.Create();
        var inv = a.Link.OpenPairing();
        var wrong = inv.Code == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < PairingInvitation.MaxFailures; i++)
            await b.Link.JoinAsync($"127.0.0.1:{a.Port}", wrong, false, CancellationToken.None);
        var r = await b.Link.JoinAsync($"127.0.0.1:{a.Port}", inv.Code, false, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Null(a.Link.ActiveInvitation);
    }

    [Fact]
    public async Task Reconnects_after_peer_restart_and_delivers_queued_messages()
    {
        var (a, b) = await PairAsync();
        await using var _a = a;
        var bId = b.Id;
        var bPort = b.Port;
        var bPeer = b.LastPeer!;

        await b.DisposeAsync(); // "apagar" B: envía bye
        await WaitUntil(() => a.Link.State.Status != LinkStatus.Connected, "detección de desconexión");

        var queued = a.Link.Send("Mensaje mientras B está apagada", false, false);
        Assert.True(queued.Accepted);
        await WaitUntil(() => a.Delivery.TryGetValue(queued.MessageId!, out var st) && st == DeliveryState.Queued, "mensaje en espera");

        await using var b2 = Node.Create(bId, bPort, bPeer); // "encender" B con la configuración guardada
        await WaitUntil(() => a.Link.State.Status == LinkStatus.Connected && b2.Link.State.Status == LinkStatus.Connected, "reconexión");
        await WaitUntil(() => b2.Received.Count == 1, "entrega del mensaje en espera");
        Assert.Equal("Mensaje mientras B está apagada", b2.Received.First().Text);
        await WaitUntil(() => a.Delivery[queued.MessageId!] == DeliveryState.Delivered, "confirmación");
    }

    [Fact]
    public async Task Reconnects_when_peer_restarts_on_a_different_port()
    {
        // Simula "cambio de dirección": B vuelve en otro puerto; A lo encuentra porque B marca hacia A.
        var (a, b) = await PairAsync();
        await using var _a = a;
        var bId = b.Id;
        var bPeer = b.LastPeer!;
        await b.DisposeAsync();
        await WaitUntil(() => a.Link.State.Status != LinkStatus.Connected, "desconexión");

        await using var b2 = Node.Create(bId, FreePort(), bPeer);
        await WaitUntil(() => a.Link.State.Status == LinkStatus.Connected, "reconexión desde el nuevo puerto");
        await WaitUntil(() => a.LastPeer!.LastPort == b2.Port, "actualización de la última dirección conocida");
    }

    [Fact]
    public async Task Unknown_instance_is_rejected_and_cannot_hijack()
    {
        var (a, b) = await PairAsync();
        await using var _a = a;
        await using var _b = b;

        // C finge ser B (mismo InstanceId) pero no conoce la clave de vínculo.
        var fakePeer = new PeerSettings
        {
            InstanceId = a.Id,
            Name = "A",
            LastAddress = "127.0.0.1",
            LastPort = a.Port,
            ProtectedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        await using var c = Node.Create(b.Id, null, fakePeer);
        await Task.Delay(1500);
        Assert.NotEqual(LinkStatus.Connected, c.Link.State.Status);
        Assert.Equal(LinkStatus.Connected, a.Link.State.Status);

        // Y el vínculo real sigue funcionando.
        var r = a.Link.Send("sigo acá", false, false);
        await WaitUntil(() => b.Received.Any(m => m.Id == r.MessageId), "mensaje tras intento de suplantación");
        Assert.Empty(c.Received);
    }

    [Fact]
    public async Task Stranger_is_told_it_is_not_recognized()
    {
        await using var a = Node.Create();
        var fakePeer = new PeerSettings
        {
            InstanceId = a.Id,
            Name = "A",
            LastAddress = "127.0.0.1",
            LastPort = a.Port,
            ProtectedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        await using var c = Node.Create(peer: fakePeer);
        await WaitUntil(() => c.Link.State.Detail?.Contains("no reconoce") == true, "detalle de vínculo no reconocido");
    }

    [Fact]
    public async Task Garbage_on_the_port_does_not_break_the_listener()
    {
        var (a, b) = await PairAsync();
        await using var _a = a;
        await using var _b = b;

        for (var i = 0; i < 5; i++)
        {
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, a.Port);
            var stream = raw.GetStream();
            await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 });
            await Task.Delay(50);
        }
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, a.Port);
            await FrameIO.WriteFrameAsync(raw.GetStream(), System.Text.Encoding.UTF8.GetBytes("{no json"), default);
        }

        var r = b.Link.Send("todavía funciona", false, false);
        await WaitUntil(() => a.Received.Any(m => m.Id == r.MessageId), "mensaje tras basura en el puerto");
        Assert.Equal(LinkStatus.Connected, a.Link.State.Status);
    }

    [Fact]
    public async Task Unpair_disconnects_and_rejects_further_sessions()
    {
        var (a, b) = await PairAsync();
        await using var _a = a;
        await using var _b = b;
        a.Link.Unpair();
        Assert.Equal(LinkStatus.NotPaired, a.Link.State.Status);
        await WaitUntil(() => b.Link.State.Status != LinkStatus.Connected, "desconexión en B");
        await WaitUntil(() => b.Link.State.Detail?.Contains("no reconoce") == true, "B informa vínculo no reconocido");
    }

    [Fact]
    public async Task Discovery_finds_the_other_instance()
    {
        var port = FreePort();
        var idA = SettingsValidator.NewInstanceId();
        var idB = SettingsValidator.NewInstanceId();
        using var a = new DiscoveryService(idA, port, () => new DiscoveryPacket { Id = idA, Name = "A", Port = 1111 });
        using var b = new DiscoveryService(idB, port, () => new DiscoveryPacket { Id = idB, Name = "B", Port = 2222, Pairing = true });
        a.Start();
        b.Start();
        var found = await a.QueryAsync(TimeSpan.FromSeconds(3), idB, CancellationToken.None);
        var match = found.SingleOrDefault(f => f.InstanceId == idB);
        if (match == null)
        {
            // Algunas máquinas sin interfaces de red activas no enrutan multicast: no es un fallo del código.
            Assert.True(NetworkInfo.GetInterfaces().Count == 0, "No se encontró la instancia B por descubrimiento UDP");
            return;
        }
        Assert.Equal("B", match.Name);
        Assert.Equal(2222, match.Port);
        Assert.True(match.PairingOpen);
        Assert.DoesNotContain(found, f => f.InstanceId == idA); // no se encuentra a sí misma
    }
}
