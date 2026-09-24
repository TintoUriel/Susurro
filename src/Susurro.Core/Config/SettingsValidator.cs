namespace Susurro.Core.Config;

/// <summary>
/// Corrige valores fuera de rango para que una configuración editada a mano
/// o corrupta nunca impida arrancar la aplicación.
/// </summary>
public static class SettingsValidator
{
    public const int MaxNameLength = 32;

    public static AppSettings Normalize(AppSettings? s, string machineName)
    {
        s ??= new AppSettings();
        s.SchemaVersion = AppSettings.CurrentSchema;

        if (!IsValidInstanceId(s.InstanceId))
            s.InstanceId = NewInstanceId();

        s.FriendlyName = CleanName(s.FriendlyName);
        if (s.FriendlyName.Length == 0)
            s.FriendlyName = CleanName(machineName);
        if (s.FriendlyName.Length == 0)
            s.FriendlyName = "PC";

        if (s.Port is < 1024 or > 65535) s.Port = AppSettings.DefaultPort;
        if (s.DiscoveryPort is < 1024 or > 65535 || s.DiscoveryPort == s.Port)
            s.DiscoveryPort = s.Port == AppSettings.DefaultDiscoveryPort ? AppSettings.DefaultDiscoveryPort + 1 : AppSettings.DefaultDiscoveryPort;

        if (s.MainWindowLeft is double l && (double.IsNaN(l) || double.IsInfinity(l))) s.MainWindowLeft = null;
        if (s.MainWindowTop is double t && (double.IsNaN(t) || double.IsInfinity(t))) s.MainWindowTop = null;

        s.Overlay = NormalizeOverlay(s.Overlay);

        // null (archivo viejo o editado) → atajo por defecto; "" → desactivado a propósito.
        s.SendHotkey = s.SendHotkey == null ? AppSettings.DefaultHotkey : s.SendHotkey.Trim();
        if (s.SendHotkey.Length > 48) s.SendHotkey = AppSettings.DefaultHotkey;

        if (s.Peer != null)
        {
            var p = s.Peer;
            if (!IsValidInstanceId(p.InstanceId) || p.InstanceId == s.InstanceId || string.IsNullOrWhiteSpace(p.ProtectedKey))
            {
                s.Peer = null; // vínculo inválido: hay que volver a vincular
            }
            else
            {
                p.Name = CleanName(p.Name);
                if (p.Name.Length == 0) p.Name = "PC remota";
                if (p.LastPort is < 1 or > 65535) p.LastPort = 0;
                p.ManualAddress = string.IsNullOrWhiteSpace(p.ManualAddress) ? null : p.ManualAddress.Trim();
                p.LastAddress = string.IsNullOrWhiteSpace(p.LastAddress) ? null : p.LastAddress.Trim();
            }
        }
        return s;
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
