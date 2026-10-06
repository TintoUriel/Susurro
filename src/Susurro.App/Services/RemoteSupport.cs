using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Susurro.Core.Logging;

namespace Susurro.App.Services;

/// <summary>
/// Soporte remoto apoyado en Quick Assist, la asistencia remota de Microsoft que viene con Windows 11.
/// Susurro solo la <b>abre</b> (y te muestra con quién vas a hablar): la pantalla y el control los maneja
/// Quick Assist, con su propio pedido de permiso del lado de la otra persona. Susurro no captura la
/// pantalla de nadie ni le inyecta teclas o clics —eso queda en manos de una herramienta auditada y con
/// consentimiento explícito—. No agregar acá captura de pantalla ni inyección de entrada.
/// </summary>
internal static class RemoteSupport
{
    public enum Result { Launched, NotInstalled }

    /// <summary>Id de Quick Assist en la Microsoft Store (para ofrecer instalarlo si falta).</summary>
    public const string StoreId = "9P7BP5VNWKX5";

    /// <summary>Abre Quick Assist. No conecta ni controla nada: solo lanza la herramienta de Microsoft.</summary>
    public static Result LaunchQuickAssist()
    {
        // 1) El alias de ejecución: en Windows 11 queda en el PATH.
        if (TryStart("quickassist.exe")) return Result.Launched;
        // 2) Ruta directa del alias de la app, por si el PATH no lo resuelve.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "quickassist.exe");
        if (File.Exists(alias) && TryStart(alias)) return Result.Launched;
        Log.Warn("support", "Quick Assist no está disponible en esta PC");
        return Result.NotInstalled;
    }

    /// <summary>Abre la página de Quick Assist en la Microsoft Store (cuando no está instalado).</summary>
    public static void OpenStore()
    {
        try { Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?productid={StoreId}") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("support", "No se pudo abrir la Store", ex); }
    }

    private static bool TryStart(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception) { return false; } // no encontrado
        catch (Exception ex)
        {
            Log.Warn("support", "No se pudo abrir Quick Assist", ex);
            return false;
        }
    }
}
