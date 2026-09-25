using System;
using System.IO;
using System.Linq;

namespace Susurro.App.Services;

/// <summary>
/// Argumentos de línea de comandos:
///   --profile NOMBRE        configuración/registro/instancia separados (probar dos instancias en una PC)
///   --port N                puerto TCP (se guarda en la configuración del perfil)
///   --discovery-port N      puerto UDP de descubrimiento (se guarda)
///   --minimized             arrancar oculto en la bandeja
///   --autostart             lanzado por Windows al iniciar sesión (arranca oculto)
///   --set-autostart on|off  activa/desactiva el inicio con Windows y termina (lo usa el instalador)
///   --uninstall-cleanup     quita el inicio con Windows y termina (lo usa el desinstalador)
///   --updated PID           versión nueva recién instalada por la actualización automática: avisa que
///                           arrancó y espera a que termine la anterior (PID) antes de tomar su lugar
/// </summary>
internal sealed class CommandLine
{
    public string? Profile { get; private set; }
    public int? Port { get; private set; }
    public int? DiscoveryPort { get; private set; }
    public bool Minimized { get; private set; }
    public bool AutoStart { get; private set; }
    public bool? SetAutoStart { get; private set; }
    public bool UninstallCleanup { get; private set; }
    public int? UpdatedFrom { get; private set; }

    public bool IsMaintenanceCommand => SetAutoStart.HasValue || UninstallCleanup;

    public static CommandLine Parse(string[] args)
    {
        var c = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim().ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--profile":
                    var p = Next();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var clean = new string(p.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
                        if (clean.Length > 0) c.Profile = clean.Length > 24 ? clean[..24] : clean;
                    }
                    break;
                case "--port":
                    if (int.TryParse(Next(), out var port) && port is >= 1024 and <= 65535) c.Port = port;
                    break;
                case "--discovery-port":
                    if (int.TryParse(Next(), out var dport) && dport is >= 1024 and <= 65535) c.DiscoveryPort = dport;
                    break;
                case "--minimized":
                    c.Minimized = true;
                    break;
                case "--autostart":
                    c.AutoStart = true;
                    break;
                case "--set-autostart":
                    var v = Next()?.ToLowerInvariant();
                    c.SetAutoStart = v is "on" or "1" or "true";
                    break;
                case "--uninstall-cleanup":
                    c.UninstallCleanup = true;
                    break;
                case "--updated":
                    if (int.TryParse(Next(), out var pid) && pid > 0) c.UpdatedFrom = pid;
                    break;
            }
        }
        return c;
    }
}

internal static class AppPaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Susurro");

    public static string DataDirectory(string? profile) =>
        profile == null ? Root : Path.Combine(Root, "profiles", profile);

    public static string LogDirectory(string? profile) => Path.Combine(DataDirectory(profile), "logs");

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Susurro.exe");
}
