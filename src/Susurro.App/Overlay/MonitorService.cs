using System;
using System.Collections.Generic;
using System.Linq;
using Susurro.App.Native;
using Susurro.Core.Config;

namespace Susurro.App.Overlay;

/// <summary>Monitor físico: coordenadas en píxeles reales y DPI efectivo.</summary>
internal sealed record MonitorDescriptor(IntPtr Handle, string DeviceName, NativeMethods.RECT Bounds, NativeMethods.RECT WorkArea, bool IsPrimary, uint Dpi)
{
    public double Scale => Dpi / 96.0;

    /// <summary>Número visible "Monitor N" (según \\.\DISPLAYN, como en Configuración de Windows).</summary>
    public int Number
    {
        get
        {
            var digits = new string(DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }
    }

    public string Describe() =>
        $"Monitor {Number} — {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " (principal)" : "")}";
}

/// <summary>
/// Enumeración de monitores vía Win32 (sin WinForms.Screen) para obtener el DPI por monitor.
/// Se consulta en el momento de mostrar cada mensaje: soporta conectar/desconectar monitores.
/// </summary>
internal static class MonitorService
{
    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var list = new List<MonitorDescriptor>();
        NativeMethods.MonitorEnumProc callback = (IntPtr h, IntPtr hdc, ref NativeMethods.RECT r, IntPtr data) =>
        {
            var d = Describe(h);
            if (d != null) list.Add(d);
            return true;
        };
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch
        {
            // sin información: se usa el principal más abajo
        }
        GC.KeepAlive(callback);
        return list.OrderBy(m => m.Number).ThenBy(m => m.Bounds.Left).ToList();
    }

    /// <summary>Resuelve la preferencia ("auto", "primary" o nombre de dispositivo) a un monitor existente.</summary>
    public static MonitorDescriptor Resolve(string? preference)
    {
        var monitors = GetMonitors();
        MonitorDescriptor? chosen = null;

        if (string.Equals(preference, OverlaySettings.MonitorAuto, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(preference))
        {
            // Automático: el monitor donde está la ventana con la que el usuario trabaja.
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                var h = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
                chosen = monitors.FirstOrDefault(m => m.Handle == h) ?? Describe(h);
            }
        }
        else if (!string.Equals(preference, OverlaySettings.MonitorPrimary, StringComparison.OrdinalIgnoreCase))
        {
            chosen = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, preference, StringComparison.OrdinalIgnoreCase));
        }

        chosen ??= monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (chosen != null) return chosen;

        // Último recurso (no debería ocurrir): monitor principal por punto (0,0).
        var hp = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return Describe(hp) ?? new MonitorDescriptor(IntPtr.Zero, "", new NativeMethods.RECT { Right = 1920, Bottom = 1080 },
            new NativeMethods.RECT { Right = 1920, Bottom = 1040 }, true, 96);
    }

    private static MonitorDescriptor? Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(h, ref info)) return null;
        uint dpi = 96;
        try
        {
            if (NativeMethods.GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0) dpi = dx;
        }
        catch
        {
            // shcore no disponible: 96 DPI
        }
        return new MonitorDescriptor(h, info.szDevice ?? "", info.rcMonitor, info.rcWork, (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, dpi);
    }
}
