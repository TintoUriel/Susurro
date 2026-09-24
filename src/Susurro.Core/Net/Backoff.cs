namespace Susurro.Core.Net;

/// <summary>
/// Espera creciente entre intentos de reconexión (1 s → 60 s) con ±20 % de aleatoriedad
/// para que las dos PCs no se sincronicen. Con la otra PC apagada, el costo en régimen
/// es un intento TCP + un par de datagramas UDP por minuto.
/// </summary>
public sealed class Backoff
{
    public static readonly TimeSpan[] DefaultSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private readonly TimeSpan[] _steps;
    private readonly double _jitter;
    private int _index;

    public Backoff(TimeSpan[]? steps = null, double jitter = 0.2)
    {
        _steps = steps is { Length: > 0 } ? steps : DefaultSteps;
        _jitter = Math.Clamp(jitter, 0, 0.5);
    }

    public int Failures { get; private set; }

    public TimeSpan Next()
    {
        var baseDelay = _steps[Math.Min(_index, _steps.Length - 1)];
        if (_index < _steps.Length - 1) _index++;
        Failures++;
        if (_jitter <= 0) return baseDelay;
        var factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * _jitter;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    public void Reset()
    {
        _index = 0;
        Failures = 0;
    }
}
