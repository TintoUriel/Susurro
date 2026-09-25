namespace Susurro.Core.Protocol;

/// <summary>
/// Tipos de paquete del protocolo TCP (campo "t").
/// Ver docs/PROTOCOL.md para la secuencia completa.
/// </summary>
public static class PacketType
{
    // --- Handshake (texto plano, antes de autenticar) ---
    public const string Hello = "hello";        // quien marca -> quien escucha
    public const string Welcome = "welcome";    // quien escucha -> quien marca
    public const string Auth = "auth";          // prueba HMAC de quien marca
    public const string Reject = "reject";      // rechazo con motivo

    // --- Sesión (cifrado AES-GCM) ---
    public const string Ok = "ok";              // primera trama cifrada: sesión aceptada
    public const string Message = "msg";
    public const string Ack = "ack";            // confirmación: received | shown
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Profile = "profile";    // cambio de nombre amigable
    public const string Bye = "bye";            // cierre ordenado
    public const string Typing = "typing";      // está escribiendo (state: on | off)

    // --- Imágenes y archivos (solo con quien anunció la capacidad "files") ---
    public const string FileOffer = "file";     // oferta: imagen (se envía enseguida) o archivo (se envía si lo piden)
    public const string FileGet = "fileGet";    // el receptor pide el archivo
    public const string Chunk = "chunk";        // bloque de datos (offset + data)
    public const string FileEnd = "fileEnd";    // fin: hash SHA-256 de todo el contenido
    public const string FileNo = "fileNo";      // rechazado / cancelado / no disponible (reason)
}

/// <summary>Capacidades que cada lado anuncia en hello/welcome (campo "features").</summary>
public static class Features
{
    /// <summary>Sabe recibir imágenes y archivos (tramas de hasta 64 KB).</summary>
    public const string Files = "files";
}

public static class TransferKinds
{
    public const string Image = "image";
    public const string File = "file";
}

public static class TypingState
{
    public const string On = "on";
    public const string Off = "off";
}

public static class HelloMode
{
    public const string Session = "session";
}

public static class AckState
{
    public const string Received = "received";
    /// <summary>Mensaje/imagen mostrado (o cerrado, si es importante); archivo descargado.</summary>
    public const string Shown = "shown";
    /// <summary>Progreso de una descarga (offset = bytes recibidos). También mantiene viva la sesión.</summary>
    public const string Progress = "progress";
}

public static class FileNoReason
{
    public const string Declined = "declined";
    public const string Canceled = "canceled";
    public const string Expired = "expired";
    public const string Changed = "changed";
    public const string TooLarge = "toolarge";
    public const string Busy = "busy";
    public const string Error = "error";
}

public static class RejectReason
{
    public const string Version = "version";
    public const string Unknown = "unknown";          // no se aceptan conexiones de esta instancia (bloqueada)
    public const string Auth = "auth";                // prueba criptográfica o clave pública inválida
    public const string Duplicate = "duplicate";      // ya existe una sesión preferida
    public const string Busy = "busy";
    public const string Self = "self";
}

/// <summary>
/// Paquete del protocolo. Es deliberadamente "plano": un único tipo con campos opcionales,
/// serializado en JSON compacto (los nulos se omiten). Simple de versionar y de depurar.
/// </summary>
public sealed class Packet
{
    public string T { get; set; } = "";

    // Handshake
    public int? V { get; set; }
    public string? Mode { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Boot { get; set; }
    public byte[]? Nonce { get; set; }
    public byte[]? Proof { get; set; }
    /// <summary>Clave pública de identidad (SubjectPublicKeyInfo); su hash es el Id.</summary>
    public byte[]? Key { get; set; }
    public int? Port { get; set; }
    public long? Ts { get; set; }
    public string? Reason { get; set; }
    /// <summary>Capacidades opcionales (ver <see cref="Protocol.Features"/>). Las versiones anteriores lo ignoran.</summary>
    public string[]? Features { get; set; }

    // Mensajes
    public string? MsgId { get; set; }
    public long? Seq { get; set; }
    public string? Text { get; set; }
    public bool? Urgent { get; set; }
    /// <summary>El remitente quiere confirmación de "mostrado".</summary>
    public bool? Receipt { get; set; }
    public string? State { get; set; }

    // Imágenes y archivos
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? Kind { get; set; }
    public long? Size { get; set; }
    public long? Offset { get; set; }
    public byte[]? Data { get; set; }
    public byte[]? Hash { get; set; }
}

/// <summary>Datagrama UDP de descubrimiento.</summary>
public sealed class DiscoveryPacket
{
    public const string Magic = "susurro";
    public const string Query = "q";
    public const string Response = "r";
    public const string Announce = "a";

    public string S { get; set; } = Magic;
    public int V { get; set; } = ProtocolConstants.Version;
    public string T { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int Port { get; set; }
    public string? Nonce { get; set; }
}

public static class ProtocolConstants
{
    /// <summary>2: identidad por clave pública, sin código de vinculación.</summary>
    public const int Version = 2;
    /// <summary>Tamaño máximo de trama antes de autenticar (limita abuso desde la red).</summary>
    public const int MaxHandshakeFrame = 4 * 1024;
    /// <summary>
    /// Tamaño máximo de trama en sesión. Un mensaje ocupa &lt; 2 KB; un bloque de archivo (32 KB en
    /// base64) ≈ 44 KB. Las 2.0.0 aceptan hasta 16 KB: a ellas nunca se les mandan bloques.
    /// </summary>
    public const int MaxSessionFrame = 64 * 1024;
    public const int MaxDatagram = 1024;
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
