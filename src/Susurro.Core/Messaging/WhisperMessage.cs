namespace Susurro.Core.Messaging;

/// <summary>Mensaje recibido (o de prueba) listo para mostrarse en el overlay.</summary>
public sealed record WhisperMessage(
    string Id,
    string Text,
    string SenderName,
    DateTimeOffset SentAt,
    bool Urgent,
    long Seq,
    bool WantsReceipt,
    bool IsTest = false,
    string? SenderId = null)
{
    /// <summary>Orden de llegada local (lo asigna la cola; desempata mensajes sin secuencia comparable).</summary>
    public long ArrivalOrder { get; init; }
}

/// <summary>Estado de entrega de un mensaje enviado, del punto de vista del remitente.</summary>
public enum DeliveryState
{
    /// <summary>Sin conexión: esperando para enviarse (expira a los 2 minutos).</summary>
    Queued,
    /// <summary>Escrito en la conexión, sin confirmación todavía.</summary>
    Sent,
    /// <summary>La otra PC confirmó que lo recibió.</summary>
    Delivered,
    /// <summary>La otra PC confirmó que lo mostró en pantalla.</summary>
    Shown,
    /// <summary>No se pudo entregar (expiró sin conexión o fue rechazado).</summary>
    Failed,
}
