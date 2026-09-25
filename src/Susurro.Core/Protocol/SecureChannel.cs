using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Susurro.Core.Protocol;

/// <summary>
/// Cifrado autenticado de la sesión (AES-256-GCM).
/// - Una clave por dirección, derivada con HKDF de la clave de enlace y de los nonces
///   aleatorios de ambos extremos (clave nueva en cada conexión).
/// - El nonce de GCM es un contador implícito de 64 bits: una trama repetida, reordenada,
///   omitida o inyectada hace fallar la verificación y se cierra la sesión.
/// No es thread-safe: el llamador serializa <see cref="Seal"/> (lock de envío) y
/// <see cref="Open"/> (un único lector).
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int TagSize = 16;
    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private int _disposed;

    private SecureChannel(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(recvKey);
    }

    public static SecureChannel Create(byte[] linkKey, bool isDialer, byte[] nonceDialer, byte[] nonceListener)
    {
        var salt = new byte[nonceDialer.Length + nonceListener.Length];
        nonceDialer.CopyTo(salt, 0);
        nonceListener.CopyTo(salt, nonceDialer.Length);

        var d2l = HKDF.DeriveKey(HashAlgorithmName.SHA256, linkKey, 32, salt, Encoding.ASCII.GetBytes("susurro/v2/session/d2l"));
        var l2d = HKDF.DeriveKey(HashAlgorithmName.SHA256, linkKey, 32, salt, Encoding.ASCII.GetBytes("susurro/v2/session/l2d"));
        return isDialer ? new SecureChannel(d2l, l2d) : new SecureChannel(l2d, d2l);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + TagSize];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sendCounter++);
        _send.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize));
        return output;
    }

    /// <summary>Descifra y verifica. Lanza <see cref="CryptographicException"/> si la trama no es auténtica.</summary>
    public byte[] Open(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < TagSize) throw new CryptographicException("Trama demasiado corta");
        var plaintext = new byte[frame.Length - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _recvCounter);
        _recv.Decrypt(nonce, frame[..^TagSize], frame[^TagSize..], plaintext);
        _recvCounter++;
        return plaintext;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _send.Dispose();
        _recv.Dispose();
    }
}
