using System;
using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Interop;
using Susurro.App.Native;

namespace Susurro.App.Services;

internal static class WindowStyling
{
    /// <summary>Borde oscuro y esquinas redondeadas nativas (Windows 11). En Windows 10 no hace nada.</summary>
    public static void ApplyDarkFrame(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var on = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                var round = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                var border = 0x00342E2C; // COLORREF 0x00BBGGRR → #2C2E34
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch
            {
                // DWM no disponible
            }
        };
    }
}

internal static class MemoryTrimmer
{
    /// <summary>
    /// Compacta el heap y devuelve páginas no usadas al sistema. Se llama solo tras eventos
    /// puntuales (ventana oculta, overlay terminado), nunca de forma periódica.
    /// </summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            NativeMethods.EmptyWorkingSet(p.Handle);
        }
        catch
        {
        }
    }
}
