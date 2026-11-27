using System;
using System.Runtime.InteropServices;
using Susurro.App.Native;

namespace Susurro.App.Services;

/// <summary>
/// Inyecta eventos de mouse y teclado en la sesión local usando SendInput.
/// Los coordenados se reciben relativos a la pantalla remota y se escalan a la local.
/// </summary>
internal static class InputInjector
{
    public static void MoveMouse(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
    }

    public static void MouseButton(int button, bool down)
    {
        uint flags = (button, down) switch
        {
            (0, true) => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            (0, false) => NativeMethods.MOUSEEVENTF_LEFTUP,
            (1, true) => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            (1, false) => NativeMethods.MOUSEEVENTF_RIGHTUP,
            (2, true) => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flags == 0) return;
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = flags } }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public static void MouseWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) } }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public static void KeyPress(int virtualKey, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
                }
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
