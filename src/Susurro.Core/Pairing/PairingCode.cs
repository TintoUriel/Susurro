using System.Security.Cryptography;
using System.Text;

namespace Susurro.Core.Pairing;

/// <summary>
/// Código de vinculación de 8 caracteres en base32 de Crockford (40 bits de entropía),
/// mostrado como "K7QM-4XPD". Al normalizar se ignoran guiones/espacios y se aceptan
/// confusiones típicas (O→0, I/L→1) y minúsculas.
/// </summary>
public static class PairingCode
{
    public const int Length = 8;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(Length);
        foreach (var b in bytes) sb.Append(Alphabet[b & 31]);
        return sb.ToString();
    }

    public static string Format(string normalized) =>
        normalized.Length == Length ? normalized[..4] + "-" + normalized[4..] : normalized;

    /// <summary>Devuelve el código normalizado (8 caracteres del alfabeto) o null si es inválido.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var sb = new StringBuilder(Length);
        foreach (var raw in input.ToUpperInvariant())
        {
            if (raw is '-' or ' ' or '_' or '.') continue;
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            if (Alphabet.IndexOf(c) < 0) return null;
            sb.Append(c);
        }
        return sb.Length == Length ? sb.ToString() : null;
    }
}
