namespace Susurro.Core.Config;

public enum OverlayPosition
{
    BottomCenter,
    TopCenter,
    Center,
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight,
}

public enum UrgentStyle
{
    /// <summary>Borde y etiqueta en color ámbar suave.</summary>
    Accent,
    /// <summary>Texto en negrita, sin color adicional.</summary>
    Bold,
    /// <summary>Fondo con un tinte cálido muy tenue.</summary>
    Tinted,
}

public enum SubtitleColor
{
    White,
    /// <summary>Amarillo clásico de subtítulos de cine/TV.</summary>
    Yellow,
    LightGray,
}

public sealed class OverlaySettings
{
    public const string MonitorAuto = "auto";
    public const string MonitorPrimary = "primary";
    public static readonly int[] AllowedDurations = { 2, 5, 8, 10 };

    /// <summary>"auto" (monitor de la ventana activa), "primary" o nombre de dispositivo (\\.\DISPLAY2).</summary>
    public string Monitor { get; set; } = MonitorAuto;
    public OverlayPosition Position { get; set; } = OverlayPosition.BottomCenter;
    public int DurationSeconds { get; set; } = 5;
    public double FontSize { get; set; } = 22;
    /// <summary>Opacidad del fondo (0.3–1). El texto siempre es opaco.</summary>
    public double Opacity { get; set; } = 0.86;
    /// <summary>Ancho máximo del overlay en % del ancho del monitor.</summary>
    public int MaxWidthPercent { get; set; } = 60;
    /// <summary>Distancia al borde (arriba/abajo) en % del alto del área de trabajo.</summary>
    public int EdgeMarginPercent { get; set; } = 9;
    public bool Animations { get; set; } = true;
    public bool HighContrast { get; set; }
    public bool ShowSenderName { get; set; } = true;
    public UrgentStyle UrgentStyle { get; set; } = UrgentStyle.Accent;
    /// <summary>Recuadro semitransparente detrás del texto. Sin él, el texto flota como un subtítulo de película.</summary>
    public bool ShowBackground { get; set; } = true;
    /// <summary>Contorno/sombra oscura alrededor de las letras (recomendado sin recuadro).</summary>
    public bool TextOutline { get; set; }
    public SubtitleColor TextColor { get; set; } = SubtitleColor.White;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();
}

public sealed class PeerSettings
{
    /// <summary>InstanceId persistente de la otra PC (no depende de su IP).</summary>
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Última dirección IPv4 donde se la encontró (solo una pista; siempre se revalida).</summary>
    public string? LastAddress { get; set; }
    public int LastPort { get; set; }
    /// <summary>Dirección configurada a mano (IP, IP:puerto o nombre de equipo). Opcional.</summary>
    public string? ManualAddress { get; set; }
    /// <summary>Clave de vínculo (32 bytes) protegida con DPAPI del usuario actual, en base64.</summary>
    public string ProtectedKey { get; set; } = "";
    public DateTime PairedUtc { get; set; }

    public PeerSettings Clone() => (PeerSettings)MemberwiseClone();
}

public sealed class AppSettings
{
    public const int CurrentSchema = 1;
    public const int DefaultPort = 47810;
    public const int DefaultDiscoveryPort = 47811;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public string InstanceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public int DiscoveryPort { get; set; } = DefaultDiscoveryPort;

    /// <summary>Activado por defecto: Windows inicia → Susurro arranca oculto en la bandeja → conecta solo.</summary>
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Mostrar "Entregado"/"Visto" al enviar (pide confirmación de lectura al receptor).</summary>
    public bool ConfirmDelivery { get; set; } = true;
    /// <summary>
    /// Atajo global para abrir Susurro y escribir desde cualquier programa ("Ctrl+Shift+Space").
    /// Cadena vacía = desactivado.
    /// </summary>
    public string SendHotkey { get; set; } = DefaultHotkey;
    public const string DefaultHotkey = "Ctrl+Shift+Space";

    /// <summary>true cuando el usuario ya pasó por la pantalla inicial.</summary>
    public bool SetupCompleted { get; set; }

    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }

    public PeerSettings? Peer { get; set; }
    public OverlaySettings Overlay { get; set; } = new();

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Peer = Peer?.Clone();
        copy.Overlay = Overlay.Clone();
        return copy;
    }
}
