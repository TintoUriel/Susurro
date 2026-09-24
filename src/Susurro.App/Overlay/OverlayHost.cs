using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Susurro.App.Native;
using Susurro.Core.Config;
using Susurro.Core.Messaging;

namespace Susurro.App.Overlay;

/// <summary>
/// Un "subtítulo" flotante para un único mensaje.
///
/// Se implementa con un <see cref="HwndSource"/> (no un Window de WPF) para controlar exactamente
/// los estilos Win32:
///  - WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE: nunca roba el foco del teclado.
///  - WS_EX_TRANSPARENT + ventana en capas: los clics atraviesan el overlay.
///  - WS_EX_TOOLWINDOW sin propietario: no aparece en Alt+Tab ni en la barra de tareas.
///  - WS_EX_TOPMOST: por encima de las ventanas normales.
/// Se crea directamente sobre el monitor destino, así WPF adopta el DPI de ese monitor
/// (PerMonitorV2) y el tamaño/posición se calculan en píxeles físicos con ese mismo DPI.
/// Se destruye al terminar cada mensaje: en reposo no queda ninguna ventana ni superficie en memoria.
/// </summary>
internal sealed class OverlayHost : IDisposable
{
    private readonly HwndSource _source;
    private readonly Border _card;
    private readonly TranslateTransform _shift = new();
    private readonly bool _animate;
    private bool _disposed;

    public OverlayHost(WhisperMessage message, OverlaySettings settings)
    {
        var monitor = MonitorService.Resolve(settings.Monitor);
        _animate = settings.Animations && SystemParameters.ClientAreaAnimation;

        var parameters = new HwndSourceParameters("SusurroOverlay")
        {
            WindowStyle = NativeMethods.WS_POPUP,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW |
                                  NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT,
            UsesPerPixelTransparency = true,
            // Crear la ventana (oculta) dentro del monitor destino para heredar su DPI.
            PositionX = monitor.WorkArea.Left + monitor.WorkArea.Width / 2,
            PositionY = monitor.WorkArea.Top + monitor.WorkArea.Height / 2,
            Width = 1,
            Height = 1,
        };
        _source = new HwndSource(parameters) { SizeToContent = SizeToContent.Manual };
        _source.AddHook(WndProc);
        EnsureExStyles(_source.Handle);

        _card = SubtitleVisual.Build(message, settings);
        _card.RenderTransform = _shift;
        var root = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
        root.Children.Add(_card);
        _source.RootVisual = root;

        Layout(root, monitor, settings);
    }

    public IntPtr Handle => _source.Handle;

    public void Show()
    {
        if (_disposed) return;
        if (_animate)
        {
            _card.Opacity = 0;
            _shift.Y = 8;
        }
        NativeMethods.ShowWindow(_source.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        if (_animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _card.BeginAnimation(UIElement.OpacityProperty, Freeze(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease }));
            _shift.BeginAnimation(TranslateTransform.YProperty, Freeze(new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease }));
        }
    }

    public void Hide(Action completed)
    {
        if (_disposed)
        {
            completed();
            return;
        }
        if (!_animate)
        {
            Dispose();
            completed();
            return;
        }
        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            Dispose();
            completed();
        };
        _card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _card.BeginAnimation(UIElement.OpacityProperty, null);
            _shift.BeginAnimation(TranslateTransform.YProperty, null);
            _source.RemoveHook(WndProc);
            _source.RootVisual = null;
            _source.Dispose();
        }
        catch
        {
            // la ventana ya no existe
        }
    }

    /// <summary>
    /// Calcula tamaño y posición en píxeles físicos del monitor destino. Si el texto no entra con el
    /// tamaño elegido (monitor chico + fuente grande), reduce la fuente hasta que entre: nunca se corta.
    /// </summary>
    private void Layout(Grid root, MonitorDescriptor monitor, OverlaySettings s)
    {
        var scale = _source.CompositionTarget?.TransformToDevice.M11 ?? monitor.Scale;
        if (scale <= 0) scale = monitor.Scale;

        var work = monitor.WorkArea;
        var maxWidthDip = Math.Max(200, work.Width / scale * s.MaxWidthPercent / 100.0);
        var maxHeightDip = Math.Max(80, work.Height / scale * 0.6);
        _card.MaxWidth = maxWidthDip;

        var fontSize = s.FontSize;
        Size desired;
        while (true)
        {
            SubtitleVisual.ApplyFontSize(_card, fontSize);
            _card.Measure(new Size(maxWidthDip, double.PositiveInfinity));
            desired = _card.DesiredSize;
            if (desired.Height <= maxHeightDip || fontSize <= 11) break;
            fontSize = Math.Max(11, fontSize * 0.9);
        }

        var widthDip = Math.Ceiling(Math.Min(desired.Width, maxWidthDip));
        var heightDip = Math.Ceiling(Math.Min(desired.Height, maxHeightDip));
        root.Width = widthDip;
        root.Height = heightDip;
        _card.Width = widthDip;
        _card.Height = heightDip;

        var w = (int)Math.Ceiling(widthDip * scale);
        var h = (int)Math.Ceiling(heightDip * scale);
        var marginY = (int)Math.Round(work.Height * s.EdgeMarginPercent / 100.0);
        var marginX = (int)Math.Round(Math.Max(24 * scale, work.Width * 0.02));

        var centerX = work.Left + (work.Width - w) / 2;
        var leftX = work.Left + marginX;
        var rightX = work.Right - marginX - w;
        var topY = work.Top + marginY;
        var bottomY = work.Bottom - marginY - h;
        var centerY = work.Top + (work.Height - h) / 2;

        var (x, y) = s.Position switch
        {
            OverlayPosition.TopCenter => (centerX, topY),
            OverlayPosition.Center => (centerX, centerY),
            OverlayPosition.BottomLeft => (leftX, bottomY),
            OverlayPosition.BottomRight => (rightX, bottomY),
            OverlayPosition.TopLeft => (leftX, topY),
            OverlayPosition.TopRight => (rightX, topY),
            _ => (centerX, bottomY),
        };
        // Nunca fuera del área visible.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    private static void EnsureExStyles(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
              NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            case NativeMethods.WM_NCHITTEST:
                handled = true;
                return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
