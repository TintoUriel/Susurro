using System.Security.Cryptography;
using System.Text;

namespace Susurro.Core.Protocol;

/// <summary>
/// Autenticación mutua desafío-respuesta con la clave de enlace (HMAC-SHA256).
/// La clave nunca viaja por la red; cada lado demuestra que la conoce firmando
/// los nonces aleatorios de ambos extremos y los identificadores de instancia.
/// </summary>
public static class HandshakeCrypto
{
    public const int NonceSize = 16;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static string NewBootId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <param name="role">'D' para quien marca, 'L' para quien escucha.</param>
    public static byte[] SessionProof(byte[] linkKey, char role, string dialerId, string listenerId, byte[] nonceDialer, byte[] nonceListener)
    {
        var data = Concat(
            Encoding.ASCII.GetBytes("susurro/v2/auth/" + role),
            Encoding.UTF8.GetBytes(dialerId),
            Encoding.UTF8.GetBytes(listenerId),
            nonceDialer,
            nonceListener);
        return HMACSHA256.HashData(linkKey, data);
    }

    public static bool FixedTimeEquals(byte[]? expected, byte[]? actual) =>
        expected != null && actual != null && expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(expected, actual);

    /// <summary>Concatenación con prefijo de longitud (evita ambigüedades entre campos).</summary>
    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => 4 + p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset), part.Length);
            offset += 4;
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
