namespace Susurro.Core.Pairing;

/// <summary>
/// Código activo en la PC que "muestra el código". Expira a los 5 minutos y se invalida
/// tras 5 intentos fallidos (protección contra adivinación en línea).
/// </summary>
public sealed class PairingInvitation
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private int _failures;

    public PairingInvitation(string normalizedCode, DateTime expiresUtc)
    {
        Code = normalizedCode;
        ExpiresUtc = expiresUtc;
    }

    public static PairingInvitation Create(TimeSpan? lifetime = null) =>
        new(PairingCode.Generate(), DateTime.UtcNow + (lifetime ?? DefaultLifetime));

    public string Code { get; }
    public DateTime ExpiresUtc { get; }
    public int Failures => Volatile.Read(ref _failures);
    public bool IsExhausted => Failures >= MaxFailures;

    public bool IsValid(DateTime utcNow) => utcNow < ExpiresUtc && !IsExhausted;

    /// <summary>Registra un intento fallido. Devuelve true si con este se agotaron los intentos.</summary>
    public bool RegisterFailure() => Interlocked.Increment(ref _failures) >= MaxFailures;
}
