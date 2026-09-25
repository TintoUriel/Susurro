using System.Security.Cryptography;
using System.Text;
using Susurro.Core.Logging;
using Susurro.Core.Protocol;

namespace Susurro.Core.Identity;

/// <summary>
/// Identidad criptográfica de esta instalación (reemplaza a la vinculación con código):
/// - Un par de claves ECDH P-256 que se genera una sola vez; la privada se guarda con DPAPI.
/// - El InstanceId ES el hash de la clave pública: nadie puede presentarse con el id de otra
///   PC sin tener su clave privada.
/// - La clave de cada par de PCs sale de ECDH(privada propia, pública de la otra) y nunca viaja
///   por la red. Con ella se hace la autenticación mutua y el cifrado de siempre (HMAC + AES-GCM).
/// </summary>
public sealed class LocalIdentity : IDisposable
{
    private readonly ECDiffieHellman _key;

    private LocalIdentity(ECDiffieHellman key)
    {
        _key = key;
        PublicKey = key.PublicKey.ExportSubjectPublicKeyInfo();
        Id = IdFromPublicKey(PublicKey);
    }

    public string Id { get; }
    /// <summary>Clave pública (SubjectPublicKeyInfo DER).</summary>
    public byte[] PublicKey { get; }

    public static LocalIdentity Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Recupera la identidad guardada o crea una nueva si no hay o no se puede leer.</summary>
    /// <param name="protectedKey">Clave privada protegida (base64), o null.</param>
    /// <param name="updatedProtectedKey">Lo que hay que guardar (igual al de entrada si no cambió).</param>
    public static LocalIdentity LoadOrCreate(string? protectedKey, IKeyProtector protector, out string updatedProtectedKey)
    {
        if (!string.IsNullOrEmpty(protectedKey))
        {
            var pkcs8 = protector.Unprotect(protectedKey);
            if (pkcs8 != null)
            {
                try
                {
                    var ecdh = ECDiffieHellman.Create();
                    ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
                    if (ecdh.KeySize == 256)
                    {
                        updatedProtectedKey = protectedKey;
                        return new LocalIdentity(ecdh);
                    }
                    ecdh.Dispose();
                }
                catch (CryptographicException) { }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }
            Log.Warn("identity", "No se pudo recuperar la identidad guardada (¿otro usuario de Windows o archivo copiado?); se crea una nueva");
        }

        var created = Create();
        var raw = created._key.ExportPkcs8PrivateKey();
        updatedProtectedKey = protector.Protect(raw);
        CryptographicOperations.ZeroMemory(raw);
        Log.Info("identity", $"Identidad nueva creada (id {created.Id[..8]}…)");
        return created;
    }

    /// <summary>InstanceId = primeros 128 bits de SHA-256(clave pública), en hexadecimal.</summary>
    public static string IdFromPublicKey(ReadOnlySpan<byte> publicKey) =>
        Convert.ToHexString(SHA256.HashData(publicKey), 0, 16).ToLowerInvariant();

    /// <summary>¿Esta clave pública corresponde a este InstanceId?</summary>
    public static bool Matches(string? id, byte[]? publicKey) =>
        id != null && publicKey is { Length: > 0 and < 512 } &&
        string.Equals(IdFromPublicKey(publicKey), id, StringComparison.Ordinal);

    /// <summary>
    /// Clave de enlace (32 bytes) con otra PC: HKDF(ECDH(propia, otra), ids ordenados).
    /// Ambos lados obtienen la misma. Lanza <see cref="CryptographicException"/> si la clave pública no es válida.
    /// </summary>
    public byte[] DeriveLinkKey(string peerId, byte[] peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out var read);
        if (read != peerPublicKey.Length || peer.KeySize != 256) throw new CryptographicException("Clave pública inválida");
        var shared = _key.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        try
        {
            var (first, second) = string.CompareOrdinal(Id, peerId) <= 0 ? (Id, peerId) : (peerId, Id);
            var salt = HandshakeCrypto.Concat(Encoding.ASCII.GetBytes(first), Encoding.ASCII.GetBytes(second));
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.ASCII.GetBytes("susurro/v2/link"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public void Dispose() => _key.Dispose();
}
