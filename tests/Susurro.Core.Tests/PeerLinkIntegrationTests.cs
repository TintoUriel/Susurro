using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Discovery;
using Susurro.Core.Identity;
using Susurro.Core.Messaging;
using Susurro.Core.Net;
using Susurro.Core.Protocol;

namespace Susurro.Core.Tests;

/// <summary>
/// Pruebas de extremo a extremo con sockets TCP reales en 127.0.0.1: varias instancias de
/// <see cref="PeerLink"/> se encuentran, se agregan como contactos sin código, intercambian
/// mensajes y se reconectan.
/// </summary>
public class PeerLinkIntegrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    internal sealed class Node : IAsyncDisposable
    {
        public readonly ConcurrentQueue<WhisperMessage> Received = new();
        public readonly ConcurrentDictionary<string, DeliveryState> Delivery = new();
        public volatile IReadOnlyList<ContactSettings> Saved = Array.Empty<ContactSettings>();

        private Node(string name, LocalIdentity identity, int port, IEnumerable<ContactSettings>? contacts)
        {
            Identity = identity;
            Port = port;
            Link = new PeerLink(new PeerLinkOptions
            {
                Identity = identity,
                LocalName = name,
                Port = port,
                EnableDiscovery = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                BackoffSteps = new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400) },
                OutboxTtl = TimeSpan.FromSeconds(30),
            });
            Link.MessageReceived += m => Received.Enqueue(m);
            Link.DeliveryChanged += (id2, st) => Delivery.AddOrUpdate(id2, st, (_, old) => st > old || st == DeliveryState.Queued ? st : old);
            Link.ContactsSaved += c => Saved = c;
            if (contacts != null) Link.SetContacts(contacts);
            Link.Start();
        }

        public LocalIdentity Identity { get; }
        public string Id => Identity.Id;
        public int Port { get; }
        public PeerLink Link { get; }
        public string Address => $"127.0.0.1:{Port}";

        public static Node Create(string name = "N", LocalIdentity? identity = null, int? port = null, IEnumerable<ContactSettings>? contacts = null) =>
            new(name, identity ?? LocalIdentity.Create(), port ?? FreePort(), contacts);

        public ContactStatus? StatusOf(Node other) => Link.FindContact(other.Id)?.Status;

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

    /// <summary>B se conecta con A por dirección (lo mismo que pasa solo con el descubrimiento).</summary>
    private static async Task Connect(Node from, Node to)
    {
        var result = await from.Link.AddByAddressAsync(to.Address, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(to.Id, result.Contact!.Id);
        await WaitUntil(() => from.StatusOf(to) == ContactStatus.Online && to.StatusOf(from) == ContactStatus.Online, "conexión");
    }

    [Fact]
    public async Task Strangers_connect_without_a_code_and_exchange_messages_with_receipts()
    {
        await using var a = Node.Create("Ana");
        await using var b = Node.Create("Beto");
        Assert.Equal(LinkStatus.NoContacts, a.Link.State.Status);

        await Connect(b, a);

        // Ambos se agregaron como contactos con el nombre de la persona, y se guardaron.
        Assert.Equal("Beto", a.Link.FindContact(b.Id)!.Name);
        Assert.Equal("Ana", b.Link.FindContact(a.Id)!.Name);
        await WaitUntil(() => a.Saved.Any(c => c.InstanceId == b.Id) && b.Saved.Any(c => c.InstanceId == a.Id), "contactos guardados");
        Assert.Equal(LinkStatus.Online, a.Link.State.Status);
        Assert.Equal(1, a.Link.State.Online);

        var send = a.Link.Send(b.Id, "  ¿Podés venir?  ", urgent: true, wantReceipt: true);
        Assert.True(send.Accepted, send.Error);
        await WaitUntil(() => b.Received.Count == 1, "recepción");
        Assert.True(b.Received.TryPeek(out var msg));
        Assert.Equal("¿Podés venir?", msg!.Text);
        Assert.True(msg.Urgent);
        Assert.True(msg.WantsReceipt);
        Assert.Equal("Ana", msg.SenderName);
        Assert.Equal(a.Id, msg.SenderId);

        await WaitUntil(() => a.Delivery.TryGetValue(send.MessageId!, out var st) && st == DeliveryState.Delivered, "confirmación de entrega");
        b.Link.ReportShown(msg);
        await WaitUntil(() => a.Delivery[send.MessageId!] == DeliveryState.Shown, "confirmación de mostrado");

        // Y en sentido inverso.
        var back = b.Link.Send(a.Id, "Voy", false, false);
        await WaitUntil(() => a.Received.Count == 1, "respuesta");
        Assert.Equal("Voy", a.Received.First().Text);
        await WaitUntil(() => b.Delivery[back.MessageId!] == DeliveryState.Delivered, "entrega de la respuesta");
    }

    [Fact]
    public async Task Messages_reach_only_the_chosen_person()
    {
        await using var a = Node.Create("Ana");
        await using var b = Node.Create("Beto");
        await using var c = Node.Create("Caro");
        await Connect(b, a);
        await Connect(c, a);
        await WaitUntil(() => a.Link.State.Online == 2, "dos personas conectadas");
        Assert.Equal(new[] { "Beto", "Caro" }, a.Link.Contacts.Select(x => x.Name));

        var toB = a.Link.Send(b.Id, "solo para Beto", false, false);
        var toC = a.Link.Send(c.Id, "solo para Caro", false, false);
        await WaitUntil(() => a.Delivery.TryGetValue(toB.MessageId!, out var s1) && s1 == DeliveryState.Delivered &&
                              a.Delivery.TryGetValue(toC.MessageId!, out var s2) && s2 == DeliveryState.Delivered, "entregas");
        Assert.Equal(new[] { "solo para Beto" }, b.Received.Select(m => m.Text));
        Assert.Equal(new[] { "solo para Caro" }, c.Received.Select(m => m.Text));

        // Cualquiera puede escribirle a cualquiera de sus contactos.
        b.Link.Send(a.Id, "hola Ana", false, false);
        c.Link.Send(a.Id, "hola Ana, soy Caro", false, false);
        await WaitUntil(() => a.Received.Count == 2, "mensajes de dos personas");
        Assert.Equal(new[] { "Beto", "Caro" }, a.Received.Select(m => m.SenderName).OrderBy(n => n));
    }

    [Fact]
    public async Task Invalid_messages_are_rejected_locally()
    {
        await using var a = Node.Create();
        await using var b = Node.Create();
        Assert.False(a.Link.Send(b.Id, "hola", false, false).Accepted); // todavía no es un contacto
        await Connect(a, b);
        Assert.False(a.Link.Send(b.Id, "   ", false, false).Accepted);
        Assert.False(a.Link.Send(b.Id, new string('a', MessageRules.MaxLength + 1), false, false).Accepted);
    }

    [Fact]
    public async Task Name_change_reaches_contacts()
    {
        await using var a = Node.Create("Ana");
        await using var b = Node.Create("Beto");
        await Connect(b, a);
        b.Link.UpdateLocalName("Roberto");
        await WaitUntil(() => a.Link.FindContact(b.Id)?.Name == "Roberto", "nombre nuevo");
    }

    [Fact]
    public async Task Reconnects_after_peer_restart_and_delivers_queued_messages()
    {
        await using var a = Node.Create("Ana");
        var b = Node.Create("Beto");
        await Connect(b, a);
        await WaitUntil(() => b.Saved.Count == 1, "contactos guardados en B");
        var bIdentity = b.Identity;
        var bPort = b.Port;
        var bContacts = b.Saved;

        await b.DisposeAsync(); // "apagar" B: envía bye
        await WaitUntil(() => a.StatusOf(b) != ContactStatus.Online, "detección de desconexión");

        var queued = a.Link.Send(b.Id, "Mensaje mientras B está apagada", false, false);
        Assert.True(queued.Accepted);
        await WaitUntil(() => a.Delivery.TryGetValue(queued.MessageId!, out var st) && st == DeliveryState.Queued, "mensaje en espera");

        // "Encender" B con la misma identidad y los contactos guardados.
        await using var b2 = Node.Create("Beto", bIdentity, bPort, bContacts);
        await WaitUntil(() => a.StatusOf(b2) == ContactStatus.Online && b2.StatusOf(a) == ContactStatus.Online, "reconexión");
        await WaitUntil(() => b2.Received.Count == 1, "entrega del mensaje en espera");
        Assert.Equal("Mensaje mientras B está apagada", b2.Received.First().Text);
        await WaitUntil(() => a.Delivery[queued.MessageId!] == DeliveryState.Delivered, "confirmación");
    }

    [Fact]
    public async Task Reconnects_when_peer_restarts_on_a_different_port()
    {
        // Simula "cambio de dirección": B vuelve en otro puerto y marca hacia A, que actualiza la dirección.
        await using var a = Node.Create("Ana");
        var b = Node.Create("Beto");
        await Connect(b, a);
        await WaitUntil(() => b.Saved.Count == 1, "contactos guardados en B");
        var bIdentity = b.Identity;
        var bContacts = b.Saved;
        await b.DisposeAsync();
        await WaitUntil(() => a.StatusOf(b) != ContactStatus.Online, "desconexión");

        await using var b2 = Node.Create("Beto", bIdentity, FreePort(), bContacts);
        await WaitUntil(() => a.StatusOf(b2) == ContactStatus.Online, "reconexión desde el nuevo puerto");
        await WaitUntil(() => a.Saved.FirstOrDefault(c => c.InstanceId == b2.Id)?.LastPort == b2.Port, "actualización de la última dirección conocida");
    }

    [Fact]
    public async Task Nobody_can_use_someone_elses_id()
    {
        await using var a = Node.Create("Ana");
        await using var b = Node.Create("Beto");
        await Connect(b, a);

        // 1) Id de B con otra clave pública: rechazado antes de responder.
        using var mallory = LocalIdentity.Create();
        var r1 = await RawHelloAsync(a.Port, new Packet
        {
            T = PacketType.Hello, V = ProtocolConstants.Version, Mode = HelloMode.Session,
            Id = b.Id, Name = "Beto", Key = mallory.PublicKey, Nonce = HandshakeCrypto.NewNonce(), Port = 1,
        });
        Assert.Equal(PacketType.Reject, r1.T);
        Assert.Equal(RejectReason.Auth, r1.Reason);

        // 2) Id y clave pública de B, pero sin su clave privada: no puede producir la prueba.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, a.Port);
            var stream = tcp.GetStream();
            await FrameIO.WriteFrameAsync(stream, SusurroJson.Serialize(new Packet
            {
                T = PacketType.Hello, V = ProtocolConstants.Version, Mode = HelloMode.Session,
                Id = b.Id, Name = "Beto", Key = b.Identity.PublicKey, Nonce = HandshakeCrypto.NewNonce(), Port = 1,
            }), default);
            var welcome = SusurroJson.Deserialize((await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxHandshakeFrame, default))!);
            Assert.Equal(PacketType.Welcome, welcome.T);
            await FrameIO.WriteFrameAsync(stream, SusurroJson.Serialize(new Packet { T = PacketType.Auth, Proof = RandomNumberGenerator.GetBytes(32) }), default);
            var reply = SusurroJson.Deserialize((await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxHandshakeFrame, default))!);
            Assert.Equal(PacketType.Reject, reply.T);
            Assert.Equal(RejectReason.Auth, reply.Reason);
        }

        // El contacto real sigue conectado y funcionando.
        Assert.Equal(ContactStatus.Online, a.StatusOf(b));
        var r = a.Link.Send(b.Id, "sigo acá", false, false);
        await WaitUntil(() => b.Received.Any(m => m.Id == r.MessageId), "mensaje tras intento de suplantación");
        Assert.Single(a.Link.Contacts);
    }

    [Fact]
    public async Task Old_protocol_version_is_rejected()
    {
        await using var a = Node.Create();
        var reply = await RawHelloAsync(a.Port, new Packet
        {
            T = PacketType.Hello, V = 1, Mode = HelloMode.Session, Id = SettingsValidator.NewInstanceId(), Nonce = HandshakeCrypto.NewNonce(),
        });
        Assert.Equal(PacketType.Reject, reply.T);
        Assert.Equal(RejectReason.Version, reply.Reason);
    }

    [Fact]
    public async Task Blocked_person_cannot_connect_or_send_until_unblocked()
    {
        await using var a = Node.Create("Ana");
        await using var b = Node.Create("Beto");
        await Connect(b, a);

        a.Link.SetBlocked(b.Id, true);
        Assert.True(a.Link.FindContact(b.Id)!.Blocked);
        Assert.Equal(LinkStatus.NoContacts, a.Link.State.Status); // los bloqueados no cuentan
        Assert.False(a.Link.Send(b.Id, "hola", false, false).Accepted);
        await WaitUntil(() => b.StatusOf(a) != ContactStatus.Online, "B desconectado");

        var queued = b.Link.Send(a.Id, "¿me leés?", false, false);
        Assert.True(queued.Accepted);
        await WaitUntil(() => b.Link.FindContact(a.Id)?.Detail?.Contains("No acepta") == true, "B informa que A no acepta la conexión");
        await Task.Delay(500);
        Assert.Empty(a.Received);
        await WaitUntil(() => a.Saved.Any(c => c.InstanceId == b.Id && c.Blocked), "bloqueo guardado");

        a.Link.SetBlocked(b.Id, false);
        await WaitUntil(() => a.Received.Any(m => m.Id == queued.MessageId), "mensaje entregado tras desbloquear");
    }

    [Fact]
    public async Task Forget_removes_an_offline_contact()
    {
        await using var a = Node.Create("Ana");
        var b = Node.Create("Beto");
        await Connect(b, a);
        await b.DisposeAsync();
        await WaitUntil(() => a.StatusOf(b) != ContactStatus.Online, "desconexión");

        a.Link.Forget(b.Id);
        Assert.Empty(a.Link.Contacts);
        await WaitUntil(() => a.Saved.Count == 0, "lista guardada vacía");
    }

    [Fact]
    public async Task Adding_an_address_that_is_not_listening_fails_clearly()
    {
        await using var a = Node.Create();
        var r = await a.Link.AddByAddressAsync($"127.0.0.1:{FreePort()}", CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("No se pudo conectar", r.Error);

        var self = await a.Link.AddByAddressAsync(a.Address, CancellationToken.None);
        Assert.False(self.Success);
        Assert.Contains("este mismo", self.Error);
    }

    [Fact]
    public async Task Garbage_on_the_port_does_not_break_the_listener()
    {
        await using var a = Node.Create();
        await using var b = Node.Create();
        await Connect(b, a);

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

        var r = b.Link.Send(a.Id, "todavía funciona", false, false);
        await WaitUntil(() => a.Received.Any(m => m.Id == r.MessageId), "mensaje tras basura en el puerto");
        Assert.Equal(ContactStatus.Online, a.StatusOf(b));
    }

    [Fact]
    public async Task Discovery_finds_the_other_instance()
    {
        var port = FreePort();
        var idA = SettingsValidator.NewInstanceId();
        var idB = SettingsValidator.NewInstanceId();
        using var a = new DiscoveryService(idA, port, () => new DiscoveryPacket { Id = idA, Name = "A", Port = 1111 });
        using var b = new DiscoveryService(idB, port, () => new DiscoveryPacket { Id = idB, Name = "B", Port = 2222 });
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
        Assert.DoesNotContain(found, f => f.InstanceId == idA); // no se encuentra a sí misma
    }

    private static async Task<Packet> RawHelloAsync(int port, Packet hello)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await FrameIO.WriteFrameAsync(stream, SusurroJson.Serialize(hello), default);
        var frame = await FrameIO.ReadFrameAsync(stream, ProtocolConstants.MaxHandshakeFrame, default);
        return SusurroJson.Deserialize(frame!);
    }
}
