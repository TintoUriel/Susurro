using System.Security.Cryptography;
using System.Text;
using Susurro.Core.Protocol;

namespace Susurro.Core.Pairing;

/// <summary>
/// Criptografía de la vinculación:
/// 1. Intercambio ECDH efímero (P-256) → secreto compartido.
/// 2. Transcript = SHA-256 de ids, claves públicas y nonces de ambos lados.
/// 3. Clave de confirmación = PBKDF2(código, transcript, 20 000 iteraciones): cada intento de
///    adivinar el código cuesta caro, incluso fuera de línea.
/// 4. Cada lado envía HMAC(confirmación, rol ‖ transcript ‖ H(secreto)). Un intermediario (MITM)
///    tiene secretos distintos con cada lado y no puede producir pruebas válidas sin el código.
/// 5. Clave de vínculo = HKDF(secreto, transcript, código): 32 bytes que ambas PCs guardan.
/// </summary>
public sealed class PairingKeyExchange : IDisposable
{
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public byte[] PublicKey => _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

    public byte[] DeriveShared(byte[] peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        return _ecdh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
    }

    public void Dispose() => _ecdh.Dispose();
}

public static class PairingCrypto
{
    public const int ConfirmIterations = 20_000;

    public static byte[] Transcript(string joinerId, string hostId, byte[] joinerKey, byte[] hostKey, byte[] joinerNonce, byte[] hostNonce) =>
        SHA256.HashData(HandshakeCrypto.Concat(
            Encoding.ASCII.GetBytes("susurro/v1/pair"),
            Encoding.UTF8.GetBytes(joinerId),
            Encoding.UTF8.GetBytes(hostId),
            joinerKey, hostKey, joinerNonce, hostNonce));

    public static byte[] ConfirmKey(string normalizedCode, byte[] transcript) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalizedCode), transcript, ConfirmIterations, HashAlgorithmName.SHA256, 32);

    /// <param name="role">'J' (quien ingresa el código) o 'H' (quien lo generó).</param>
    public static byte[] ConfirmProof(byte[] confirmKey, char role, byte[] transcript, byte[] shared) =>
        HMACSHA256.HashData(confirmKey, HandshakeCrypto.Concat(
            Encoding.ASCII.GetBytes("susurro/v1/pair/confirm/" + role),
            transcript,
            SHA256.HashData(shared)));

    public static byte[] PairKey(byte[] shared, byte[] transcript, string normalizedCode) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript,
            Encoding.UTF8.GetBytes("susurro/v1/pairkey/" + normalizedCode));
}
