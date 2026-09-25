using System.Collections.Concurrent;
using System.Security.Cryptography;
using Susurro.Core.Identity;
using Susurro.Core.Messaging;
using Susurro.Core.Net;
using Susurro.Core.Protocol;
using Susurro.Core.Transfers;
using static Susurro.Core.Tests.PeerLinkIntegrationTests;

namespace Susurro.Core.Tests;

/// <summary>Imágenes, archivos y "está escribiendo", con sockets TCP reales en 127.0.0.1.</summary>
public sealed class TransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "susurro-transfer-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FNode : IAsyncDisposable
    {
        public readonly ConcurrentQueue<WhisperMessage> Received = new();
        public readonly ConcurrentDictionary<string, DeliveryState> Delivery = new();
        public readonly ConcurrentQueue<IncomingFile> Offers = new();
        public readonly ConcurrentDictionary<string, string> Completed = new();
        public readonly ConcurrentDictionary<string, string> Failed = new();
        public readonly ConcurrentDictionary<string, long> Progress = new();
        public readonly ConcurrentQueue<(string Id, string Name, bool On)> Typing = new();

        public FNode(string name, string temp, bool files = true)
        {
            Identity = LocalIdentity.Create();
            Port = FreePort();
            Temp = temp;
            Link = new PeerLink(new PeerLinkOptions
            {
                Identity = Identity,
                LocalName = name,
                Port = Port,
                EnableDiscovery = false,
                EnableFileTransfer = files,
                TransferTempDirectory = temp,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                BackoffSteps = new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400) },
            });
            Link.MessageReceived += m => Received.Enqueue(m);
            Link.DeliveryChanged += (id, st) => Delivery[id] = st;
            Link.FileOffered += f => Offers.Enqueue(f);
            Link.TransferCompleted += (id, path) => Completed[id] = path;
            Link.TransferFailed += (id, reason) => Failed[id] = reason;
            Link.TransferProgress += (id, done, _) => Progress[id] = done;
            Link.TypingChanged += (id, n, on) => Typing.Enqueue((id, n, on));
            Link.Start();
        }

        public LocalIdentity Identity { get; }
        public string Id => Identity.Id;
        public int Port { get; }
        public string Temp { get; }
        public PeerLink Link { get; }

        public ValueTask DisposeAsync() => Link.DisposeAsync();
    }

    private FNode Node(string name, bool files = true) => new(name, Path.Combine(_root, name, "tmp"), files);

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static async Task Connect(FNode from, FNode to)
    {
        var r = await from.Link.AddByAddressAsync($"127.0.0.1:{to.Port}", CancellationToken.None);
        Assert.True(r.Success, r.Error);
        await WaitUntil(() => from.Link.FindContact(to.Id)?.Status == ContactStatus.Online
                              && to.Link.FindContact(from.Id)?.Status == ContactStatus.Online, "conexión");
    }

    private string MakeFile(string name, int size)
    {
        var path = Path.Combine(Dir("origen"), name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }

    [Fact]
    public async Task Image_arrives_complete_with_caption_sender_and_receipts()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);

        var image = RandomNumberGenerator.GetBytes(200_000); // varios bloques + uno parcial
        var r = a.Link.SendImage(b.Id, image, "  mirá esto ", urgent: true, wantReceipt: true);
        Assert.True(r.Accepted, r.Error);

        await WaitUntil(() => b.Received.Any(m => m.IsImage), "imagen");
        var msg = b.Received.Single(m => m.IsImage);
        Assert.Equal(image, msg.Image);
        Assert.Equal("mirá esto", msg.Text);
        Assert.Equal("Ana", msg.SenderName);
        Assert.Equal(a.Id, msg.SenderId);
        Assert.True(msg.Urgent);

        await WaitUntil(() => a.Delivery.TryGetValue(r.MessageId!, out var st) && st == DeliveryState.Delivered, "entregada");
        b.Link.ReportShown(msg);
        await WaitUntil(() => a.Delivery[r.MessageId!] == DeliveryState.Shown, "vista");
    }

    [Fact]
    public async Task File_is_offered_then_downloaded_on_request_with_progress_and_receipt()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);

        var path = MakeFile("informe.pdf", 3 * 1024 * 1024 + 123);
        var r = a.Link.OfferFile(b.Id, path, wantReceipt: true);
        Assert.True(r.Accepted, r.Error);

        await WaitUntil(() => b.Offers.Count == 1, "oferta");
        var offer = b.Offers.Single();
        Assert.Equal(r.MessageId, offer.Id);
        Assert.Equal("informe.pdf", offer.Name);
        Assert.Equal(new FileInfo(path).Length, offer.Size);
        Assert.Equal("Ana", offer.SenderName);
        await WaitUntil(() => a.Delivery.TryGetValue(offer.Id, out var st) && st == DeliveryState.Delivered, "oferta entregada");

        // Nada viaja hasta que se pide.
        Assert.Empty(Directory.GetFiles(Dir("descargas")));

        Assert.Null(b.Link.DownloadFile(offer.Id, Dir("descargas")));
        await WaitUntil(() => b.Completed.ContainsKey(offer.Id), "descarga");
        var saved = b.Completed[offer.Id];
        Assert.Equal(Path.Combine(Dir("descargas"), "informe.pdf"), saved);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(saved));
        Assert.True(b.Progress[offer.Id] >= 1024 * 1024);
        Assert.True(a.Progress.ContainsKey(offer.Id), "quien envía ve el progreso");
        await WaitUntil(() => a.Delivery[offer.Id] == DeliveryState.Shown, "aviso de descargado");
        Assert.Empty(Directory.GetFiles(b.Temp)); // no quedan temporales
    }

    [Fact]
    public async Task Same_name_twice_is_saved_with_a_unique_name()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);
        var path = MakeFile("foto.jpg", 50_000);
        var dir = Dir("descargas");

        foreach (var expected in new[] { "foto.jpg", "foto (2).jpg" })
        {
            var r = a.Link.OfferFile(b.Id, path, false);
            await WaitUntil(() => b.Offers.Any(o => o.Id == r.MessageId), "oferta");
            Assert.Null(b.Link.DownloadFile(r.MessageId!, dir));
            await WaitUntil(() => b.Completed.ContainsKey(r.MessageId!), "descarga");
            Assert.Equal(Path.Combine(dir, expected), b.Completed[r.MessageId!]);
        }
    }

    [Fact]
    public async Task Closing_an_offer_tells_the_sender_and_it_can_no_longer_be_downloaded()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);
        var r = a.Link.OfferFile(b.Id, MakeFile("a.txt", 10), false);
        await WaitUntil(() => b.Offers.Count == 1, "oferta");

        b.Link.DeclineFile(r.MessageId!);
        await WaitUntil(() => a.Delivery.TryGetValue(r.MessageId!, out var st) && st == DeliveryState.Declined, "rechazo");
        Assert.NotNull(b.Link.DownloadFile(r.MessageId!, Dir("descargas")));
    }

    [Fact]
    public async Task File_changed_after_offering_fails_cleanly_without_leftovers()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);
        var path = MakeFile("datos.bin", 100_000);
        var r = a.Link.OfferFile(b.Id, path, false);
        await WaitUntil(() => b.Offers.Count == 1, "oferta");

        File.AppendAllText(path, "cambio");
        Assert.Null(b.Link.DownloadFile(r.MessageId!, Dir("descargas")));
        await WaitUntil(() => b.Failed.ContainsKey(r.MessageId!), "falla");
        Assert.Contains("cambió", b.Failed[r.MessageId!]);
        Assert.Empty(Directory.GetFiles(Dir("descargas")));
        Assert.Empty(Directory.Exists(b.Temp) ? Directory.GetFiles(b.Temp) : Array.Empty<string>());
    }

    [Fact]
    public async Task Disconnect_during_download_never_leaves_a_partial_file()
    {
        await using var b = Node("Beto");
        var a = Node("Ana");
        await Connect(a, b);
        var r = a.Link.OfferFile(b.Id, MakeFile("grande.bin", 24 * 1024 * 1024), false);
        await WaitUntil(() => b.Offers.Count == 1, "oferta");
        Assert.Null(b.Link.DownloadFile(r.MessageId!, Dir("descargas")));
        await WaitUntil(() => b.Progress.ContainsKey(r.MessageId!) || b.Completed.ContainsKey(r.MessageId!), "empezó");
        await a.DisposeAsync(); // se "apaga" a mitad de camino

        await WaitUntil(() => b.Completed.ContainsKey(r.MessageId!) || b.Failed.ContainsKey(r.MessageId!), "fin");
        if (b.Failed.ContainsKey(r.MessageId!))
        {
            Assert.Empty(Directory.GetFiles(Dir("descargas")));
            Assert.Empty(Directory.Exists(b.Temp) ? Directory.GetFiles(b.Temp) : Array.Empty<string>());
        }
        else
        {
            Assert.Equal(24 * 1024 * 1024, new FileInfo(b.Completed[r.MessageId!]).Length);
        }
    }

    [Fact]
    public async Task Older_version_without_file_support_gets_a_clear_error_but_text_still_works()
    {
        await using var a = Node("Ana");
        await using var b = Node("Beto", files: false);
        await Connect(a, b);

        var img = a.Link.SendImage(b.Id, new byte[] { 1, 2, 3 }, null, false, false);
        Assert.False(img.Accepted);
        Assert.Contains("versión anterior", img.Error);
        Assert.False(a.Link.OfferFile(b.Id, MakeFile("x.txt", 5), false).Accepted);

        var text = a.Link.Send(b.Id, "igual me llega", false, false);
        await WaitUntil(() => b.Received.Any(m => m.Id == text.MessageId), "texto");
    }

    [Fact]
    public async Task Images_and_files_need_the_person_connected_and_respect_limits()
    {
        await using var a = Node("Ana");
        var b = Node("Beto");
        await Connect(a, b);
        var tooBig = a.Link.SendImage(b.Id, new byte[TransferLimits.MaxImageBytes + 1], null, false, false);
        Assert.False(tooBig.Accepted);

        await b.DisposeAsync();
        await WaitUntil(() => a.Link.FindContact(b.Id)?.Status != ContactStatus.Online, "desconexión");
        var r = a.Link.SendImage(b.Id, new byte[] { 1 }, null, false, false);
        Assert.False(r.Accepted);
        Assert.Contains("no está conectado", r.Error);
    }

    [Fact]
    public async Task Typing_indicator_reaches_the_person_and_clears_on_disconnect()
    {
        var a = Node("Ana");
        await using var b = Node("Beto");
        await Connect(a, b);

        a.Link.SendTyping(new[] { b.Id }, true);
        await WaitUntil(() => b.Typing.Any(t => t.Id == a.Id && t.On && t.Name == "Ana"), "escribiendo");
        a.Link.SendTyping(new[] { b.Id }, false);
        await WaitUntil(() => b.Typing.Count(t => t.Id == a.Id && !t.On) >= 1, "dejó de escribir");

        a.Link.SendTyping(new[] { b.Id }, true);
        await WaitUntil(() => b.Typing.Count(t => t.On) >= 2, "escribiendo otra vez");
        await a.DisposeAsync();
        await WaitUntil(() => b.Typing.Last() is { On: false }, "se borra al desconectarse");
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil.exe", "evil.exe")]
    [InlineData("C:/Users/x/informe final.pdf", "informe final.pdf")]
    [InlineData("a.txt:secreto", "secreto")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("foto.jpg.   ", "foto.jpg")]
    [InlineData("  ", "archivo")]
    [InlineData("..", "archivo")]
    [InlineData("fac\u202Etxt.exe", "factxt.exe")]
    [InlineData("a<b>c|d?.txt", "abcd.txt")]
    public void File_names_from_the_network_are_sanitized(string input, string expected)
    {
        Assert.Equal(expected, FileNames.Sanitize(input));
    }

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var clean = FileNames.Sanitize(new string('x', 300) + ".docx");
        Assert.Equal(FileNames.MaxLength, clean.Length);
        Assert.EndsWith(".docx", clean);
    }

    [Fact]
    public void A_full_chunk_fits_in_a_session_frame()
    {
        var p = new Packet { T = PacketType.Chunk, FileId = MessageRules.NewMessageId(), Offset = long.MaxValue, Data = new byte[TransferLimits.ChunkSize] };
        var bytes = SusurroJson.Serialize(p).Length + SecureChannel.TagSize;
        Assert.True(bytes < ProtocolConstants.MaxSessionFrame, $"{bytes} bytes");
    }
}
