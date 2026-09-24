namespace Susurro.Core.Messaging;

/// <summary>
/// Cola de mensajes para el overlay: nunca se muestran dos a la vez.
/// Orden: los urgentes pendientes pasan delante de los normales pendientes (sin interrumpir
/// al que se está mostrando); dentro de la misma prioridad, por hora de envío y luego por
/// número de secuencia, lo que corrige mensajes que llegaran fuera de orden.
/// Capacidad acotada: si se llena, se descarta el pendiente normal más antiguo.
/// No es thread-safe: se usa desde el hilo de UI.
/// </summary>
public sealed class DisplayQueue
{
    private readonly List<WhisperMessage> _pending = new();
    private readonly int _capacity;
    private long _arrival;

    public DisplayQueue(int capacity = 20) => _capacity = Math.Max(1, capacity);

    public WhisperMessage? Current { get; private set; }
    public int PendingCount => _pending.Count;
    public IReadOnlyList<WhisperMessage> Pending => _pending;

    /// <summary>Encola. Devuelve el mensaje descartado por capacidad, si lo hubo.</summary>
    public WhisperMessage? Enqueue(WhisperMessage message)
    {
        message = message with { ArrivalOrder = ++_arrival };
        WhisperMessage? dropped = null;
        if (_pending.Count >= _capacity)
        {
            var idx = _pending.FindIndex(m => !m.Urgent);
            if (idx < 0) idx = 0;
            dropped = _pending[idx];
            _pending.RemoveAt(idx);
        }

        var insertAt = _pending.Count;
        for (var i = 0; i < _pending.Count; i++)
        {
            if (Compare(message, _pending[i]) < 0)
            {
                insertAt = i;
                break;
            }
        }
        _pending.Insert(insertAt, message);
        return dropped;
    }

    /// <summary>Si no hay nada en pantalla, toma el siguiente pendiente y lo marca como actual.</summary>
    public WhisperMessage? BeginNext()
    {
        if (Current != null || _pending.Count == 0) return null;
        Current = _pending[0];
        _pending.RemoveAt(0);
        return Current;
    }

    public void CompleteCurrent() => Current = null;

    public void Clear()
    {
        _pending.Clear();
        Current = null;
    }

    private static int Compare(WhisperMessage a, WhisperMessage b)
    {
        if (a.Urgent != b.Urgent) return a.Urgent ? -1 : 1;
        // Mismo remitente (pruebas aparte): por hora de envío; si son casi simultáneos
        // (o hay desfase de reloj de hasta 1 s), por número de secuencia.
        if (!a.IsTest && !b.IsTest && a.SenderName == b.SenderName)
        {
            if (Math.Abs((a.SentAt - b.SentAt).TotalSeconds) > 1)
                return a.SentAt.CompareTo(b.SentAt);
            var bySeq = a.Seq.CompareTo(b.Seq);
            if (bySeq != 0) return bySeq;
        }
        return a.ArrivalOrder.CompareTo(b.ArrivalOrder);
    }
}
