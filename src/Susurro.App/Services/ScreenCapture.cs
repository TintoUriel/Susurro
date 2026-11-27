using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Susurro.App.Native;

namespace Susurro.App.Services;

/// <summary>
/// Captura la pantalla principal con GDI (BitBlt) y la comprime a JPEG.
/// No mantiene estado: cada llamada a <see cref="Capture"/> devuelve una nueva captura.
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private readonly int _quality;
    private bool _disposed;

    public ScreenCapture(int jpegQuality = 40)
    {
        _quality = Math.Clamp(jpegQuality, 10, 90);
    }

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    /// <summary>Captura la pantalla principal y devuelve los bytes JPEG, o null si falla.</summary>
    public byte[]? Capture()
    {
        if (_disposed) return null;
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (w <= 0 || h <= 0) return null;
        ScreenWidth = w;
        ScreenHeight = h;

        var hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;
        var hdcMem = IntPtr.Zero;
        var hBitmap = IntPtr.Zero;
        try
        {
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, w, h);
            var old = NativeMethods.SelectObject(hdcMem, hBitmap);
            NativeMethods.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, 0, 0, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(hdcMem, old);

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            var encoder = new JpegBitmapEncoder { QualityLevel = _quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(hdcMem);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
