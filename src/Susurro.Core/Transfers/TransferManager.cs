using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;
using Susurro.Core.Net;
using Susurro.Core.Protocol;

namespace Susurro.Core.Transfers;

public static class TransferLimits
{
    /// <summary>Datos por bloque (≈ 44 KB en base64, dentro de la trama de 64 KB).</summary>
    public const int ChunkSize = 32 * 1024;
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>Memoria total para imágenes recibiéndose a la vez (protege ante abusos).</summary>
    public const long MaxIncomingImageMemory = 40L * 1024 * 1024;
    /// <summary>Cada cuántos bytes el receptor confirma el progreso (también mantiene viva la sesión).</summary>
    public const long ProgressAckEvery = 1024 * 1024;
    public const int MaxOffersPerRecipient = 20;
    public static readonly TimeSpan FileOfferLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ImageOfferLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>Archivo que otra persona ofrece (todavía no descargado).</summary>
public sealed record IncomingFile(string Id, string SenderId, string SenderName, string Name, long Size, DateTimeOffset SentAt);

/// <summary>
/// Envío y recepción de imágenes y archivos por la sesión cifrada, en bloques de 32 KB.
/// - Imagen: oferta + datos enseguida; el receptor la arma en memoria (máx. 10 MB) y la muestra.
/// - Archivo: solo la oferta; los datos viajan cuando el receptor elige "Descargar". Se escribe en un
///   temporal y al terminar (hash SHA-256 correcto) se mueve a la carpeta elegida.
/// Todo se valida antes de reservar memoria o escribir: id, tamaño declarado, offset exacto de cada
/// bloque, que no se exceda el tamaño y el hash final. Nunca se confía en el nombre recibido.
/// Sin temporizadores: todo avanza por paquetes recibidos o acciones del usuario.
/// </summary>
internal sealed class TransferManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Outgoing> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Incoming> _in = new(StringComparer.Ordinal);
    private readonly DuplicateFilter _seen = new(1024);
    private readonly Func<string, PeerSession?> _readySession;
    private readonly string _tempDir;
    private long _imageMemory;

    public TransferManager(Func<string, PeerSession?> readySession, string tempDirectory)
    {
        _readySession = readySession;
        _tempDir = tempDirectory;
    }

    /// <summary>Imagen recibida completa (con remitente, pie de foto e importancia).</summary>
    public event Action<WhisperMessage>? ImageReceived;
    public event Action<IncomingFile>? FileOffered;
    /// <summary>Progreso (ambos lados): id, bytes, total.</summary>
    public event Action<string, long, long>? Progress;
    /// <summary>Descarga completa: id, ruta final.</summary>
    public event Action<string, string>? Completed;
    /// <summary>Descarga fallida o cancelada por la otra PC: id, motivo legible.</summary>
    public event Action<string, string>? Failed;
    /// <summary>Estado del lado de quien envía.</summary>
    public event Action<string, DeliveryState>? Delivery;

    // ------------------------------------------------------------------ enviar

    public SendResult SendImage(PeerSession session, Packet offerTemplate, byte[] image)
    {
        if (image.Length == 0) return new SendResult(false, null, "La imagen está vacía.");
        if (image.Length > TransferLimits.MaxImageBytes)
            return new SendResult(false, null, $"La imagen es muy grande (máximo {FileNames.FormatSize(TransferLimits.MaxImageBytes)}).");
        var o = new Outgoing(MessageRules.NewMessageId(), session.PeerId, "imagen.png", image.Length, TransferKinds.Image,
            image, null, DateTime.UtcNow + TransferLimits.ImageOfferLifetime);
        if (!Register(o, out var error)) return new SendResult(false, null, error);
        var offer = Offer(offerTemplate, o);
        _ = SendOfferAsync(session, o, offer, streamNow: true);
        return new SendResult(true, o.Id, null);
    }

    public SendResult OfferFile(PeerSession session, Packet offerTemplate, string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return new SendResult(false, null, "El archivo ya no existe.");
        }
        catch (Exception ex)
        {
            return new SendResult(false, null, "No se pudo leer el archivo: " + ex.Message);
        }
        if (info.Length > TransferLimits.MaxFileBytes)
            return new SendResult(false, null, $"El archivo es muy grande (máximo {FileNames.FormatSize(TransferLimits.MaxFileBytes)}).");
        var o = new Outgoing(MessageRules.NewMessageId(), session.PeerId, FileNames.Sanitize(info.Name), info.Length, TransferKinds.File,
            null, info.FullName, DateTime.UtcNow + TransferLimits.FileOfferLifetime);
        if (!Register(o, out var error)) return new SendResult(false, null, error);
        _ = SendOfferAsync(session, o, Offer(offerTemplate, o), streamNow: false);
        return new SendResult(true, o.Id, null);
    }

    private bool Register(Outgoing o, out string? error)
    {
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (_out.Values.Count(x => x.PeerId == o.PeerId) >= TransferLimits.MaxOffersPerRecipient)
            {
                error = "Demasiados archivos pendientes para esa persona.";
                return false;
            }
            _out[o.Id] = o;
        }
        error = null;
        return true;
    }

    private static Packet Offer(Packet template, Outgoing o) => new()
    {
        T = PacketType.FileOffer,
        FileId = o.Id,
        FileName = o.Name,
        Size = o.Size,
        Kind = o.Kind,
        Text = template.Text,
        Urgent = template.Urgent,
        Receipt = template.Receipt,
        Seq = template.Seq,
        Ts = template.Ts,
        Name = template.Name,
    };

    private async Task SendOfferAsync(PeerSession session, Outgoing o, Packet offer, bool streamNow)
    {
        if (!await session.SendAsync(offer).ConfigureAwait(false))
        {
            Remove(o.Id);
            Delivery?.Invoke(o.Id, DeliveryState.Failed);
            return;
        }
        Log.Info("files", $"{(o.Kind == TransferKinds.Image ? "Imagen" : "Archivo")} ofrecido a {session.PeerName} [{o.Id[..8]}] ({FileNames.FormatSize(o.Size)})");
        Delivery?.Invoke(o.Id, DeliveryState.Sent);
        if (streamNow) await StreamAsync(session, o).ConfigureAwait(false);
    }

    private async Task StreamAsync(PeerSession session, Outgoing o)
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (o.Cts != null) return; // ya se está enviando
            o.Cts = new CancellationTokenSource();
            ct = o.Cts.Token;
        }
        var ok = false;
        try
        {
            await using Stream src = o.Bytes != null
                ? new MemoryStream(o.Bytes, writable: false)
                : new FileStream(o.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (src.Length != o.Size)
            {
                await session.SendAsync(No(o.Id, FileNoReason.Changed)).ConfigureAwait(false);
                Log.Warn("files", $"«{o.Name}» cambió desde que se ofreció [{o.Id[..8]}]");
                return;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[TransferLimits.ChunkSize];
            long offset = 0;
            while (offset < o.Size)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, o.Size - offset);
                var n = await src.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("El archivo se acortó mientras se enviaba");
                hash.AppendData(buffer, 0, n);
                if (!await session.SendAsync(new Packet { T = PacketType.Chunk, FileId = o.Id, Offset = offset, Data = buffer.AsSpan(0, n).ToArray() }).ConfigureAwait(false))
                    return; // la sesión se cerró: lo informa OnSessionClosed
                offset += n;
            }
            ok = await session.SendAsync(new Packet { T = PacketType.FileEnd, FileId = o.Id, Hash = hash.GetHashAndReset() }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("files", $"No se pudo leer «{o.Name}» para enviarlo", ex);
            await session.SendAsync(No(o.Id, FileNoReason.Error)).ConfigureAwait(false);
            Delivery?.Invoke(o.Id, DeliveryState.Failed);
        }
        finally
        {
            lock (_gate)
            {
                o.Cts?.Dispose();
                o.Cts = null;
            }
            // Las imágenes se envían una sola vez: se liberan los bytes, pero el registro queda
            // (hasta que expira) para reconocer el "Entregado" y el "Visto". Los archivos quedan
            // disponibles para descargar hasta que expiran.
            if (o.Kind == TransferKinds.Image)
            {
                if (ok) o.Bytes = null;
                else Remove(o.Id);
            }
            if (ok) Log.Info("files", $"Envío completo [{o.Id[..8]}]");
        }
    }

    // ------------------------------------------------------------------ recibir (acciones del usuario)

    /// <summary>Empieza (o reintenta) la descarga de un archivo ofrecido. Devuelve un error legible o null.</summary>
    public string? Download(string id, string directory)
    {
        Incoming? inc;
        lock (_gate) _in.TryGetValue(id, out inc);
        if (inc == null) return "Ese archivo ya no está disponible.";
        var session = _readySession(inc.PeerId);
        if (session == null) return $"{inc.SenderName} no está conectado ahora.";
        lock (inc)
        {
            if (inc.State == TransferState.Downloading) return null;
            try
            {
                Directory.CreateDirectory(_tempDir);
                Directory.CreateDirectory(directory);
                inc.PartPath = Path.Combine(_tempDir, inc.Id + ".part");
                inc.Target = new FileStream(inc.PartPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            }
            catch (Exception ex)
            {
                return "No se pudo crear el archivo: " + ex.Message;
            }
            inc.TargetDirectory = directory;
            inc.Received = 0;
            inc.LastProgressAck = 0;
            inc.Hash?.Dispose();
            inc.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            inc.State = TransferState.Downloading;
        }
        Log.Info("files", $"Descargando «{inc.Name}» de {inc.SenderName} [{id[..8]}]");
        _ = SendOrFail(session, new Packet { T = PacketType.FileGet, FileId = id }, inc);
        return null;
    }

    /// <summary>Cerrar la oferta sin descargar (o cancelar la descarga en curso).</summary>
    public void Decline(string id)
    {
        Incoming? inc;
        lock (_gate)
        {
            if (!_in.Remove(id, out inc)) return;
        }
        var downloading = inc.State == TransferState.Downloading;
        Abort(inc);
        var session = _readySession(inc.PeerId);
        if (session != null) _ = session.SendAsync(No(id, downloading ? FileNoReason.Canceled : FileNoReason.Declined));
    }

    private async Task SendOrFail(PeerSession session, Packet p, Incoming inc)
    {
        if (!await session.SendAsync(p).ConfigureAwait(false)) Fail(inc, $"{inc.SenderName} se desconectó.");
    }

    // ------------------------------------------------------------------ paquetes

    /// <summary>Procesa un paquete de imágenes/archivos. Devuelve false si no era de este módulo.</summary>
    public bool Handle(PeerSession session, Packet p)
    {
        switch (p.T)
        {
            case PacketType.FileOffer: HandleOffer(session, p); return true;
            case PacketType.FileGet: HandleGet(session, p); return true;
            case PacketType.Chunk: HandleChunk(session, p); return true;
            case PacketType.FileEnd: HandleEnd(session, p); return true;
            case PacketType.FileNo: HandleNo(session, p); return true;
            case PacketType.Ack when p.MsgId != null && IsOutgoing(p.MsgId, session.PeerId): HandleAck(p); return true;
            default: return false;
        }
    }

    private bool IsOutgoing(string id, string peerId)
    {
        lock (_gate) return _out.TryGetValue(id, out var o) && o.PeerId == peerId;
    }

    private void HandleOffer(PeerSession session, Packet p)
    {
        var id = p.FileId;
        if (!MessageRules.IsValidMessageId(id) || p.Size is not long size || size < 0) return;
        var isImage = p.Kind == TransferKinds.Image;
        if (!isImage && p.Kind != TransferKinds.File) return;

        if (!_seen.TryRegister(id!))
        {
            // Oferta repetida (reconexión): se vuelve a confirmar, sin duplicar nada.
            if (!isImage) _ = session.SendAsync(Ack(id!, AckState.Received));
            return;
        }
        var limit = isImage ? TransferLimits.MaxImageBytes : TransferLimits.MaxFileBytes;
        if (size > limit || (isImage && size == 0))
        {
            _ = session.SendAsync(No(id!, FileNoReason.TooLarge));
            return;
        }

        var sender = SettingsValidator.CleanName(p.Name);
        if (sender.Length == 0) sender = session.PeerName;
        var sentAt = p.Ts is long ts ? DateTimeOffset.FromUnixTimeMilliseconds(ts - session.ClockOffsetMs) : DateTimeOffset.UtcNow;
        var inc = new Incoming(id!, session.PeerId, sender, FileNames.Sanitize(p.FileName, isImage ? "imagen.png" : "archivo"), size, isImage)
        {
            Caption = MessageRules.Sanitize(p.Text) is { Length: > 0 and <= MessageRules.MaxLength } c ? c : null,
            Urgent = p.Urgent == true,
            Receipt = p.Receipt == true,
            Seq = p.Seq ?? 0,
            SentAt = sentAt,
        };

        if (isImage)
        {
            lock (_gate)
            {
                if (_imageMemory + size > TransferLimits.MaxIncomingImageMemory)
                {
                    _ = session.SendAsync(No(id!, FileNoReason.Busy));
                    return;
                }
                _imageMemory += size;
                _in[inc.Id] = inc;
            }
            inc.Target = new MemoryStream((int)size);
            inc.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            inc.State = TransferState.Downloading; // las imágenes llegan solas, a continuación
            return;
        }

        lock (_gate) _in[inc.Id] = inc;
        _ = session.SendAsync(Ack(inc.Id, AckState.Received));
        Log.Info("files", $"{sender} ofrece «{inc.Name}» ({FileNames.FormatSize(size)}) [{inc.Id[..8]}]");
        FileOffered?.Invoke(new IncomingFile(inc.Id, inc.PeerId, sender, inc.Name, size, sentAt));
    }

    private void HandleGet(PeerSession session, Packet p)
    {
        Outgoing? o;
        lock (_gate)
        {
            PurgeExpiredLocked();
            _out.TryGetValue(p.FileId ?? "", out o);
        }
        if (o == null || o.PeerId != session.PeerId || o.Kind != TransferKinds.File)
        {
            _ = session.SendAsync(No(p.FileId ?? "", FileNoReason.Expired));
            return;
        }
        _ = StreamAsync(session, o);
    }

    private void HandleChunk(PeerSession session, Packet p)
    {
        var inc = FindIncoming(p.FileId, session.PeerId);
        if (inc == null) return; // bloque no pedido: se ignora
        string? error = null;
        long received = 0;
        var sendProgress = false;
        lock (inc)
        {
            if (inc.State != TransferState.Downloading || inc.Target == null || inc.Hash == null) return;
            var data = p.Data;
            if (data is not { Length: > 0 } || data.Length > TransferLimits.ChunkSize || p.Offset != inc.Received || inc.Received + data.Length > inc.Size)
            {
                error = "Se recibieron datos inválidos.";
            }
            else
            {
                try
                {
                    inc.Target.Write(data, 0, data.Length);
                    inc.Hash.AppendData(data);
                    inc.Received += data.Length;
                    received = inc.Received;
                    if (!inc.IsImage && received - inc.LastProgressAck >= TransferLimits.ProgressAckEvery)
                    {
                        inc.LastProgressAck = received;
                        sendProgress = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    error = "No se pudo escribir el archivo (¿disco lleno?).";
                }
            }
        }
        if (error != null)
        {
            _ = session.SendAsync(No(inc.Id, FileNoReason.Canceled));
            Fail(inc, error);
            return;
        }
        if (inc.IsImage) return;
        if (sendProgress)
        {
            _ = session.SendAsync(new Packet { T = PacketType.Ack, MsgId = inc.Id, State = AckState.Progress, Offset = received });
            Progress?.Invoke(inc.Id, received, inc.Size);
        }
    }

    private void HandleEnd(PeerSession session, Packet p)
    {
        var inc = FindIncoming(p.FileId, session.PeerId);
        if (inc == null) return;
        byte[]? content = null;
        string? error = null;
        lock (inc)
        {
            if (inc.State != TransferState.Downloading || inc.Target == null || inc.Hash == null) return;
            var hash = inc.Hash.GetHashAndReset();
            if (inc.Received != inc.Size || !HandshakeCrypto.FixedTimeEquals(hash, p.Hash))
            {
                error = "El archivo llegó incompleto o dañado.";
            }
            else if (inc.IsImage)
            {
                content = ((MemoryStream)inc.Target).ToArray();
                inc.State = TransferState.Done;
            }
            else
            {
                try
                {
                    inc.Target.Flush();
                    inc.Target.Dispose();
                    inc.Target = null;
                    var final = FileNames.UniquePath(inc.TargetDirectory!, inc.Name);
                    File.Move(inc.PartPath!, final);
                    MarkOfTheWeb.Apply(final);
                    inc.FinalPath = final;
                    inc.State = TransferState.Done;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = "No se pudo guardar el archivo: " + ex.Message;
                }
            }
        }
        if (error != null)
        {
            Fail(inc, error);
            return;
        }

        lock (_gate) _in.Remove(inc.Id);
        if (inc.IsImage)
        {
            ReleaseImageMemory(inc);
            _ = session.SendAsync(Ack(inc.Id, AckState.Received));
            Log.Info("files", $"Imagen recibida de {inc.SenderName} [{inc.Id[..8]}] ({FileNames.FormatSize(inc.Size)})");
            ImageReceived?.Invoke(new WhisperMessage(inc.Id, inc.Caption ?? "", inc.SenderName, inc.SentAt, inc.Urgent, inc.Seq,
                inc.Receipt, SenderId: inc.PeerId, Image: content));
            return;
        }
        if (inc.Receipt) _ = session.SendAsync(Ack(inc.Id, AckState.Shown));
        Log.Info("files", $"Descargado «{Path.GetFileName(inc.FinalPath)}» de {inc.SenderName} [{inc.Id[..8]}]");
        Progress?.Invoke(inc.Id, inc.Size, inc.Size);
        Completed?.Invoke(inc.Id, inc.FinalPath!);
    }

    private void HandleNo(PeerSession session, Packet p)
    {
        var id = p.FileId ?? "";
        Outgoing? o = null;
        lock (_gate)
        {
            if (_out.TryGetValue(id, out var x) && x.PeerId == session.PeerId)
            {
                o = x;
                // Si solo canceló una descarga, el archivo sigue ofrecido (puede reintentar).
                if (p.Reason != FileNoReason.Canceled) _out.Remove(id);
            }
        }
        if (o != null)
        {
            lock (_gate) o.Cts?.Cancel();
            Delivery?.Invoke(id, p.Reason == FileNoReason.Declined ? DeliveryState.Declined
                : p.Reason == FileNoReason.Canceled ? DeliveryState.Delivered : DeliveryState.Failed);
            return;
        }

        var inc = FindIncoming(id, session.PeerId);
        if (inc == null) return;
        Fail(inc, p.Reason switch
        {
            FileNoReason.Expired => "El archivo ya no está disponible en la otra PC.",
            FileNoReason.Changed => "El archivo cambió en la otra PC: pedile que lo vuelva a enviar.",
            FileNoReason.TooLarge => "El archivo es demasiado grande.",
            FileNoReason.Canceled => $"{inc.SenderName} canceló el envío.",
            _ => $"{inc.SenderName} no pudo enviar el archivo.",
        }, forget: p.Reason is FileNoReason.Expired or FileNoReason.TooLarge);
    }

    private void HandleAck(Packet p)
    {
        var id = p.MsgId!;
        switch (p.State)
        {
            case AckState.Received:
                Delivery?.Invoke(id, DeliveryState.Delivered);
                break;
            case AckState.Shown:
                // Archivo descargado o imagen vista: ya no hace falta tenerlo ofrecido.
                Remove(id);
                Delivery?.Invoke(id, DeliveryState.Shown);
                break;
            case AckState.Progress when p.Offset is long done:
                long total;
                lock (_gate) total = _out.TryGetValue(id, out var o) ? o.Size : 0;
                if (total > 0) Progress?.Invoke(id, Math.Min(done, total), total);
                break;
        }
    }

    /// <summary>Se cortó la conexión con esa persona: fallan las descargas en curso y se frenan los envíos.</summary>
    public void OnSessionClosed(PeerSession session)
    {
        List<Incoming> broken;
        List<Outgoing> stopped;
        lock (_gate)
        {
            broken = _in.Values.Where(i => i.PeerId == session.PeerId).ToList();
            stopped = _out.Values.Where(o => o.PeerId == session.PeerId && o.Cts != null).ToList();
            foreach (var o in stopped) o.Cts!.Cancel();
        }
        foreach (var inc in broken)
        {
            if (inc.IsImage) Fail(inc, "", forget: true);
            else if (inc.State == TransferState.Downloading) Fail(inc, $"{inc.SenderName} se desconectó.");
        }
        foreach (var o in stopped.Where(o => o.Kind == TransferKinds.Image)) Delivery?.Invoke(o.Id, DeliveryState.Failed);
    }

    // ------------------------------------------------------------------ utilidades

    private Incoming? FindIncoming(string? id, string peerId)
    {
        if (id == null) return null;
        lock (_gate) return _in.TryGetValue(id, out var inc) && inc.PeerId == peerId ? inc : null;
    }

    private void Fail(Incoming inc, string reason, bool forget = false)
    {
        bool wasActive;
        lock (inc)
        {
            wasActive = inc.State == TransferState.Downloading;
            inc.State = TransferState.Failed;
        }
        Abort(inc);
        if (inc.IsImage || forget)
        {
            lock (_gate) _in.Remove(inc.Id);
        }
        if (inc.IsImage)
        {
            Log.Warn("files", $"Imagen de {inc.SenderName} incompleta [{inc.Id[..8]}]");
            return;
        }
        if (wasActive || forget)
        {
            Log.Warn("files", $"Descarga de «{inc.Name}» fallida [{inc.Id[..8]}]: {reason}");
            Failed?.Invoke(inc.Id, reason);
        }
    }

    /// <summary>Cierra y borra lo parcial (el archivo temporal nunca queda a medias en Descargas).</summary>
    private void Abort(Incoming inc)
    {
        lock (inc)
        {
            try { inc.Target?.Dispose(); } catch { }
            inc.Target = null;
            inc.Hash?.Dispose();
            inc.Hash = null;
            if (inc.PartPath != null)
            {
                try { File.Delete(inc.PartPath); } catch { }
                inc.PartPath = null;
            }
            if (inc.State == TransferState.Downloading) inc.State = TransferState.Failed;
        }
        if (inc.IsImage) ReleaseImageMemory(inc);
    }

    private void ReleaseImageMemory(Incoming inc)
    {
        lock (_gate)
        {
            if (inc.MemoryReleased) return;
            inc.MemoryReleased = true;
            _imageMemory = Math.Max(0, _imageMemory - inc.Size);
        }
    }

    private void Remove(string id)
    {
        lock (_gate) _out.Remove(id);
    }

    private void PurgeExpiredLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _out.Values.Where(o => o.ExpiresUtc < now && o.Cts == null).Select(o => o.Id).ToList())
            _out.Remove(id);
    }

    private static Packet Ack(string id, string state) => new() { T = PacketType.Ack, MsgId = id, State = state };

    private static Packet No(string id, string reason) => new() { T = PacketType.FileNo, FileId = id, Reason = reason };

    private enum TransferState { Offered, Downloading, Done, Failed }

    private sealed class Outgoing
    {
        public Outgoing(string id, string peerId, string name, long size, string kind, byte[]? bytes, string? path, DateTime expiresUtc)
        {
            Id = id;
            PeerId = peerId;
            Name = name;
            Size = size;
            Kind = kind;
            Bytes = bytes;
            Path = path;
            ExpiresUtc = expiresUtc;
        }

        public string Id { get; }
        public string PeerId { get; }
        public string Name { get; }
        public long Size { get; }
        public string Kind { get; }
        public byte[]? Bytes { get; set; }
        public string? Path { get; }
        public DateTime ExpiresUtc { get; }
        public CancellationTokenSource? Cts { get; set; }
    }

    private sealed class Incoming
    {
        public Incoming(string id, string peerId, string senderName, string name, long size, bool isImage)
        {
            Id = id;
            PeerId = peerId;
            SenderName = senderName;
            Name = name;
            Size = size;
            IsImage = isImage;
        }

        public string Id { get; }
        public string PeerId { get; }
        public string SenderName { get; }
        public string Name { get; }
        public long Size { get; }
        public bool IsImage { get; }
        public string? Caption { get; init; }
        public bool Urgent { get; init; }
        public bool Receipt { get; init; }
        public long Seq { get; init; }
        public DateTimeOffset SentAt { get; init; }

        public TransferState State { get; set; } = TransferState.Offered;
        public Stream? Target { get; set; }
        public IncrementalHash? Hash { get; set; }
        public long Received { get; set; }
        public long LastProgressAck { get; set; }
        public string? PartPath { get; set; }
        public string? TargetDirectory { get; set; }
        public string? FinalPath { get; set; }
        public bool MemoryReleased { get; set; }
    }
}
