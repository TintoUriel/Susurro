namespace Susurro.Core.Config;

/// <summary>
/// Corrige valores fuera de rango para que una configuración editada a mano
/// o corrupta nunca impida arrancar la aplicación.
/// </summary>
public static class SettingsValidator
{
    public const int MaxNameLength = 32;

    public const int MaxContacts = 200;

    public static AppSettings Normalize(AppSettings? s, string machineName)
    {
        s ??= new AppSettings();
        if (s.SchemaVersion < 2)
        {
            // Versión con vinculación por código: se vuelve a pedir el nombre de la persona (que antes
            // solía ser el de la PC) y los contactos se encuentran solos por la red.
            s.SetupCompleted = false;
            if (string.Equals(CleanName(s.FriendlyName), CleanName(machineName), StringComparison.OrdinalIgnoreCase))
                s.FriendlyName = "";
        }
        s.SchemaVersion = AppSettings.CurrentSchema;

        if (!IsValidInstanceId(s.InstanceId))
            s.InstanceId = NewInstanceId();
        s.InstanceId = s.InstanceId.ToLowerInvariant();

        // Nunca se usa el nombre del equipo: lo elige la persona en la pantalla de bienvenida.
        s.FriendlyName = CleanName(s.FriendlyName);
        if (s.FriendlyName.Length == 0) s.SetupCompleted = false;

        if (s.Port is < 1024 or > 65535) s.Port = AppSettings.DefaultPort;
        if (s.DiscoveryPort is < 1024 or > 65535 || s.DiscoveryPort == s.Port)
            s.DiscoveryPort = s.Port == AppSettings.DefaultDiscoveryPort ? AppSettings.DefaultDiscoveryPort + 1 : AppSettings.DefaultDiscoveryPort;

        if (s.MainWindowLeft is double l && (double.IsNaN(l) || double.IsInfinity(l))) s.MainWindowLeft = null;
        if (s.MainWindowTop is double t && (double.IsNaN(t) || double.IsInfinity(t))) s.MainWindowTop = null;

        s.Overlay = NormalizeOverlay(s.Overlay);

        // null (archivo viejo o editado) → atajo por defecto; "" → desactivado a propósito.
        s.SendHotkey = s.SendHotkey == null ? AppSettings.DefaultHotkey : s.SendHotkey.Trim();
        if (s.SendHotkey.Length > 48) s.SendHotkey = AppSettings.DefaultHotkey;

        s.Contacts = NormalizeContacts(s.Contacts, s.InstanceId);
        if (s.LastRecipient != AppSettings.AllRecipients && !IsValidInstanceId(s.LastRecipient)) s.LastRecipient = null;
        return s;
    }

    /// <summary>Descarta contactos inválidos, repetidos o de esta misma instancia, y limita la cantidad.</summary>
    public static List<ContactSettings> NormalizeContacts(IEnumerable<ContactSettings?>? contacts, string localId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ContactSettings>();
        foreach (var c in contacts ?? Enumerable.Empty<ContactSettings?>())
        {
            if (c == null || !IsValidInstanceId(c.InstanceId)) continue;
            c.InstanceId = c.InstanceId.ToLowerInvariant();
            if (c.InstanceId == localId || !seen.Add(c.InstanceId)) continue;
            c.Name = CleanName(c.Name);
            if (c.Name.Length == 0) c.Name = "Sin nombre";
            if (c.LastPort is < 1 or > 65535) c.LastPort = 0;
            c.ManualAddress = string.IsNullOrWhiteSpace(c.ManualAddress) ? null : c.ManualAddress.Trim();
            c.LastAddress = string.IsNullOrWhiteSpace(c.LastAddress) ? null : c.LastAddress.Trim();
            result.Add(c);
        }
        // Si sobran, se quedan los bloqueados (para que sigan bloqueados) y los vistos más recientemente.
        return result.Count <= MaxContacts
            ? result
            : result.OrderByDescending(c => c.Blocked).ThenByDescending(c => c.LastSeenUtc).Take(MaxContacts).ToList();
    }

    public static OverlaySettings NormalizeOverlay(OverlaySettings? o)
    {
        o ??= new OverlaySettings();
        if (string.IsNullOrWhiteSpace(o.Monitor)) o.Monitor = OverlaySettings.MonitorAuto;
        if (!Enum.IsDefined(o.Position)) o.Position = OverlayPosition.BottomCenter;
        if (!Enum.IsDefined(o.UrgentStyle)) o.UrgentStyle = UrgentStyle.Accent;
        if (!Enum.IsDefined(o.TextColor)) o.TextColor = SubtitleColor.White;
        o.DurationSeconds = Math.Clamp(o.DurationSeconds, 1, 30);
        o.FontSize = Clamp(o.FontSize, 12, 48, 22);
        o.Opacity = Clamp(o.Opacity, 0.3, 1.0, 0.86);
        o.MaxWidthPercent = Math.Clamp(o.MaxWidthPercent, 25, 95);
        o.EdgeMarginPercent = Math.Clamp(o.EdgeMarginPercent, 0, 40);
        return o;
    }

    public static bool IsValidInstanceId(string? id) =>
        id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public static string NewInstanceId() => Guid.NewGuid().ToString("N");

    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].Trim() : clean;
    }

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, min, max);
}
