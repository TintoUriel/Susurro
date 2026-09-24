using System;
using Microsoft.Win32;
using Susurro.Core.Logging;

namespace Susurro.App.Services;

/// <summary>
/// Inicio con Windows mediante HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// (por usuario, sin permisos de administrador, sin tareas programadas ni servicios).
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName(string? profile) => profile == null ? "Susurro" : "Susurro-" + profile;

    private static string Command(string? profile) =>
        $"\"{AppPaths.ExecutablePath}\" --autostart" + (profile == null ? "" : $" --profile {profile}");

    public static bool IsEnabled(string? profile)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName(profile)) is string;
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled, string? profile)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
                key.SetValue(ValueName(profile), Command(profile), RegistryValueKind.String);
            else if (key.GetValue(ValueName(profile)) != null)
                key.DeleteValue(ValueName(profile), false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("app", "No se pudo cambiar el inicio con Windows", ex);
            return false;
        }
    }

    /// <summary>Si está activado pero apunta a otra ruta (la app se movió o actualizó), lo corrige.</summary>
    public static void Repair(bool shouldBeEnabled, string? profile)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var current = key?.GetValue(ValueName(profile)) as string;
            if (shouldBeEnabled && !string.Equals(current, Command(profile), StringComparison.OrdinalIgnoreCase))
                Set(true, profile);
            else if (!shouldBeEnabled && current != null)
                Set(false, profile);
        }
        catch
        {
            // sin acceso al registro: se ignora
        }
    }

    /// <summary>Elimina todas las entradas de Susurro (desinstalación).</summary>
    public static void RemoveAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name == "Susurro" || name.StartsWith("Susurro-", StringComparison.Ordinal))
                    key.DeleteValue(name, false);
            }
        }
        catch
        {
        }
    }
}
