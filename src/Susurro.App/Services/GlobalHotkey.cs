using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Susurro.App.Native;
using Susurro.Core.Logging;

namespace Susurro.App.Services;

/// <summary>Combinación de teclas ("Ctrl+Shift+Space") con conversión a Win32 y texto legible.</summary>
internal sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (key != null || !Enum.TryParse<Key>(raw, true, out var k)) return false;
                    key = k;
                    break;
            }
        }
        if (key is not Key parsed || !IsValid(mods, parsed)) return false;
        gesture = new HotkeyGesture(mods, parsed);
        return true;
    }

    /// <summary>Debe tener al menos un modificador y una tecla que no sea un modificador.</summary>
    public static bool IsValid(ModifierKeys mods, Key key) =>
        mods != ModifierKeys.None && key != Key.None && !IsModifierKey(key);

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>Formato para guardar ("Ctrl+Shift+Space").</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Formato para mostrar ("Ctrl + Shift + Espacio").</summary>
    public string Display()
    {
        var parts = ToString().Split('+').ToList();
        parts[^1] = Key switch
        {
            Key.Space => "Espacio",
            Key.Enter => "Enter",
            Key.Back => "Retroceso",
            Key.Tab => "Tab",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(Key - Key.NumPad0),
            _ => Key.ToString(),
        };
        return string.Join(" + ", parts);
    }

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0) |
        0x4000u; // MOD_NOREPEAT: mantener apretado no dispara varias veces

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);
}

/// <summary>
/// Atajo de teclado global con RegisterHotKey: Windows avisa con WM_HOTKEY solo cuando se pulsa
/// la combinación. No hay ganchos de teclado ni sondeo: no lee ni vigila ninguna otra tecla.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5355; // "SU"
    private const int ProbeId = 0x5356;

    private readonly HwndSource _window;
    private readonly Action _pressed;
    private HotkeyGesture? _registered;
    private bool _suspended;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        _window = new HwndSource(new HwndSourceParameters("SusurroHotkey")
        {
            WindowStyle = NativeMethods.WS_POPUP,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
    }

    public HotkeyGesture? Current { get; private set; }
    public bool IsActive => _registered != null;

    /// <summary>Registra el atajo (reemplazando el anterior). Devuelve false si Windows u otro programa ya lo usa.</summary>
    public bool Set(HotkeyGesture? gesture)
    {
        Unregister();
        Current = gesture;
        if (gesture == null || _suspended) return true;
        return Register(gesture);
    }

    /// <summary>¿Se podría registrar esta combinación? (sin cambiar el atajo actual)</summary>
    public bool CanRegister(HotkeyGesture gesture)
    {
        if (gesture == _registered) return true;
        if (!RegisterHotKey(_window.Handle, ProbeId, gesture.NativeModifiers, gesture.VirtualKey)) return false;
        UnregisterHotKey(_window.Handle, ProbeId);
        return true;
    }

    /// <summary>Libera el atajo temporalmente (p. ej. mientras se elige uno nuevo en Configuración).</summary>
    public void Suspend()
    {
        _suspended = true;
        Unregister();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        if (Current != null) Register(Current);
    }

    private bool Register(HotkeyGesture gesture)
    {
        if (RegisterHotKey(_window.Handle, HotkeyId, gesture.NativeModifiers, gesture.VirtualKey))
        {
            _registered = gesture;
            return true;
        }
        Log.Warn("hotkey", $"No se pudo registrar el atajo {gesture.Display()} (lo usa Windows u otro programa)");
        return false;
    }

    private void Unregister()
    {
        if (_registered == null) return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try { _pressed(); }
            catch (Exception ex) { Log.Error("hotkey", "Error al abrir con el atajo", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
