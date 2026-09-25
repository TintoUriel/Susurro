using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Susurro.Core.Identity;

/// <summary>Protege la clave privada de identidad en reposo.</summary>
public interface IKeyProtector
{
    string Protect(byte[] key);
    /// <summary>Devuelve null si no se puede recuperar (otro usuario/PC, dato corrupto).</summary>
    byte[]? Unprotect(string protectedKey);
}

/// <summary>
/// DPAPI (CryptProtectData) en ámbito del usuario actual: el archivo de configuración copiado
/// a otra PC u otro usuario no sirve para suplantar a esta instancia.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Susurro/identity/v2");

    public string Protect(byte[] key) => Convert.ToBase64String(Transform(key, protect: true));

    public byte[]? Unprotect(string protectedKey)
    {
        try
        {
            return Transform(Convert.FromBase64String(protectedKey), protect: false);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        var hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEntropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = hData.AddrOfPinnedObject();
            entropy.cbData = Entropy.Length;
            entropy.pbData = hEntropy.AddrOfPinnedObject();
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            hData.Free();
            hEntropy.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>Sin protección (solo para tests).</summary>
public sealed class PlainKeyProtector : IKeyProtector
{
    public string Protect(byte[] key) => Convert.ToBase64String(key);

    public byte[]? Unprotect(string protectedKey)
    {
        try { return Convert.FromBase64String(protectedKey); }
        catch { return null; }
    }
}
