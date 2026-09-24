namespace Susurro.Core.Messaging;

/// <summary>
/// Recuerda los últimos N identificadores de mensaje (memoria acotada) para descartar
/// duplicados cuando el remitente reenvía tras una reconexión.
/// </summary>
public sealed class DuplicateFilter
{
    private readonly int _capacity;
    private readonly HashSet<string> _set;
    private readonly Queue<string> _order;
    private readonly object _gate = new();

    public DuplicateFilter(int capacity = 512)
    {
        _capacity = Math.Max(1, capacity);
        _set = new HashSet<string>(_capacity, StringComparer.Ordinal);
        _order = new Queue<string>(_capacity);
    }

    public int Count { get { lock (_gate) return _set.Count; } }

    /// <summary>Registra el id. Devuelve false si ya se había visto (duplicado).</summary>
    public bool TryRegister(string id)
    {
        lock (_gate)
        {
            if (!_set.Add(id)) return false;
            _order.Enqueue(id);
            while (_order.Count > _capacity)
                _set.Remove(_order.Dequeue());
            return true;
        }
    }
}
