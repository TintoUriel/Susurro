using System.Buffers;
using System.Buffers.Binary;

namespace Susurro.Core.Protocol;

/// <summary>
/// Tramas sobre TCP: [longitud uint32 big-endian][payload].
/// El límite de tamaño se valida ANTES de reservar memoria.
/// </summary>
public static class FrameIO
{
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var total = 4 + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
            payload.CopyTo(buffer.AsMemory(4));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Lee una trama completa. Devuelve null si el otro extremo cerró limpiamente
    /// entre tramas. Lanza <see cref="ProtocolException"/> si el tamaño es inválido
    /// y <see cref="EndOfStreamException"/> si la conexión se corta a mitad de trama.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxSize, CancellationToken ct)
    {
        var header = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(header.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException("Conexión cerrada a mitad de cabecera");
            }
            read += n;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > (uint)maxSize)
            throw new ProtocolException($"Tamaño de trama inválido: {length}");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
