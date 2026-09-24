using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Susurro.App.Native;
using Susurro.Core.Logging;
using Susurro.Core.Net;

namespace Susurro.App.Tray;

/// <summary>
/// Icono en la bandeja del sistema implementado directamente con Shell_NotifyIcon
/// (sin WinForms: ~20 MB menos en disco y menos memoria).
///  - Clic izquierdo: mostrar/ocultar la ventana.
///  - Clic derecho: menú (Mostrar ventana, Ocultar ventana, Configuración, Salir) con el tema oscuro.
///  - Punto de estado en el icono: verde conectado / ámbar conectando / gris desconectado.
///  - Si el Explorador de Windows se reinicia, el icono se vuelve a agregar solo (mensaje TaskbarCreated).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly ContextMenu _menu;
    private readonly MenuItem _show;
    private readonly MenuItem _hide;
    private readonly Action _toggleMain;
    private readonly Func<bool> _isMainVisible;
    private readonly Dictionary<LinkStatus, IntPtr> _icons = new();
    private IntPtr _currentIcon;
    private string _tip = "Susurro";
    private bool _added;
    private bool _disposed;

    public TrayIcon(Action toggleMain, Action showMain, Action hideMain, Action showSettings, Action exit, Func<bool> isMainVisible)
    {
        _toggleMain = toggleMain;
        _isMainVisible = isMainVisible;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Ventana oculta de nivel superior (no "message-only": esas no reciben TaskbarCreated).
        _window = new HwndSource(new HwndSourceParameters("SusurroTray")
        {
            WindowStyle = NativeMethods.WS_POPUP,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);

        _show = new MenuItem { Header = "Mostrar ventana" };
        _show.Click += (_, _) => showMain();
        _hide = new MenuItem { Header = "Ocultar ventana" };
        _hide.Click += (_, _) => hideMain();
        var settings = new MenuItem { Header = "Configuración" };
        settings.Click += (_, _) => showSettings();
        var quit = new MenuItem { Header = "Salir" };
        quit.Click += (_, _) => exit();
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(_show);
        _menu.Items.Add(_hide);
        _menu.Items.Add(settings);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(quit);

        SetState(new LinkState(LinkStatus.Disconnected, null, null, null));
    }

    public void SetState(LinkState state)
    {
        if (_disposed) return;
        if (!_icons.TryGetValue(state.Status, out var icon))
        {
            icon = CreateIcon(state.Status);
            _icons[state.Status] = icon;
        }
        _currentIcon = icon;
        _tip = state.Status switch
        {
            LinkStatus.Connected => $"Susurro — Conectado con {state.PeerName}",
            LinkStatus.Connecting => "Susurro — Conectando…",
            LinkStatus.NotPaired => "Susurro — Sin vincular",
            _ => $"Susurro — Desconectado{(state.PeerName != null ? " de " + state.PeerName : "")}",
        };
        if (_tip.Length > 127) _tip = _tip[..126] + "…";
        Update(_added ? NIM_MODIFY : NIM_ADD);
    }

    private void Update(uint message)
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _currentIcon;
        data.szTip = _tip;
        var ok = Shell_NotifyIcon(message, ref data);
        if (message == NIM_MODIFY && !ok)
        {
            // El icono se perdió (Explorador reiniciado sin aviso): volver a agregarlo.
            message = NIM_ADD;
            ok = Shell_NotifyIcon(NIM_ADD, ref data);
        }
        if (message == NIM_ADD)
        {
            _added = ok;
            if (!ok) Log.Warn("tray", "No se pudo agregar el icono a la bandeja (¿Explorador iniciándose?)");
        }
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WM_LBUTTONUP) _toggleMain();
            else if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Log.Info("tray", "El Explorador se reinició: se vuelve a agregar el icono");
            _added = false;
            Update(NIM_ADD);
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var visible = _isMainVisible();
        _show.IsEnabled = !visible;
        _hide.IsEnabled = visible;
        // Necesario para que el menú se cierre al hacer clic fuera de él.
        NativeMethods.SetForegroundWindow(_window.Handle);
        _menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _menu.IsOpen = false;
        foreach (var h in _icons.Values) NativeMethods.DestroyIcon(h);
        _icons.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    // ------------------------------------------------------------------ dibujo del icono (WPF → PNG → HICON)

    private static IntPtr CreateIcon(LinkStatus status)
    {
        var size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        double s = size;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(Color.FromRgb(36, 37, 42));
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(70, 73, 80)), Math.Max(1, s / 24));
            dc.DrawRoundedRectangle(bg, edge, new Rect(0.5, 0.5, s - 1, s - 1), s * 0.24, s * 0.24);

            var barH = Math.Max(2, Math.Round(s * 0.13));
            var gap = Math.Max(1, Math.Round(s * 0.08));
            var y2 = s * 0.66;
            var y1 = y2 - barH - gap;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(231, 232, 234)), null,
                new Rect((s - s * 0.64) / 2 - s * 0.06, y1, s * 0.64, barH), barH / 2, barH / 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(138, 169, 214)), null,
                new Rect((s - s * 0.40) / 2 - s * 0.06, y2, s * 0.40, barH), barH / 2, barH / 2);

            var dot = status switch
            {
                LinkStatus.Connected => Color.FromRgb(111, 191, 142),
                LinkStatus.Connecting => Color.FromRgb(217, 169, 91),
                _ => Color.FromRgb(120, 123, 130),
            };
            var r = s * 0.21;
            dc.DrawEllipse(new SolidColorBrush(dot), new Pen(new SolidColorBrush(Color.FromRgb(21, 22, 25)), Math.Max(1, s / 16)),
                new Point(s - r - 0.5, s - r - 0.5), r, r);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var png = ms.ToArray();
        // Windows acepta PNG como datos de recurso de icono (desde Vista).
        return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
}
