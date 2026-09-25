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
    public int DurationSeconds { get; set; } = 8;
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

/// <summary>
/// Otra persona con Susurro en la red. Se agrega sola la primera vez que se conectan
/// (su identidad ya quedó verificada criptográficamente).
/// </summary>
public sealed class ContactSettings
{
    /// <summary>InstanceId de la otra instalación (hash de su clave pública; no depende de su IP).</summary>
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Última dirección IPv4 donde se la encontró (solo una pista; siempre se revalida).</summary>
    public string? LastAddress { get; set; }
    public int LastPort { get; set; }
    /// <summary>Dirección agregada a mano (IP, IP:puerto o nombre de equipo). Opcional.</summary>
    public string? ManualAddress { get; set; }
    /// <summary>No se aceptan conexiones ni mensajes de esta persona.</summary>
    public bool Blocked { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public ContactSettings Clone() => (ContactSettings)MemberwiseClone();
}

public sealed class AppSettings
{
    /// <summary>2: identidad por clave pública y contactos automáticos (antes: una PC vinculada con código).</summary>
    public const int CurrentSchema = 2;
    /// <summary>Valor de <see cref="LastRecipient"/> para "todos los conectados".</summary>
    public const string AllRecipients = "*";
    public const int DefaultPort = 47810;
    public const int DefaultDiscoveryPort = 47811;

    public int SchemaVersion { get; set; } = CurrentSchema;
    /// <summary>Se deriva de la clave de identidad al arrancar.</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>Clave privada de identidad protegida con DPAPI (base64).</summary>
    public string? IdentityKey { get; set; }
    /// <summary>Nombre de la persona (lo ven los demás). Vacío hasta la pantalla de bienvenida.</summary>
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

    /// <summary>InstanceId del último destinatario elegido, o <see cref="AllRecipients"/>.</summary>
    public string? LastRecipient { get; set; }

    public List<ContactSettings> Contacts { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Contacts = Contacts.Select(c => c.Clone()).ToList();
        copy.Overlay = Overlay.Clone();
        return copy;
    }
}
