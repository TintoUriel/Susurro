using System.Text;

namespace Susurro.Core.Transfers;

/// <summary>
/// Nombres de archivo recibidos de la red: nunca se confía en ellos.
/// Se descarta cualquier ruta, caracteres inválidos o de control, nombres reservados de Windows
/// y extensiones engañosas por espacios/puntos finales.
/// </summary>
public static class FileNames
{
    public const int MaxLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name, string fallback = "archivo")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        // Solo el último segmento: "..\..\evil.exe" o "C:/x/y.txt" → "evil.exe" / "y.txt".
        var last = name.Replace('\\', '/');
        var slash = last.LastIndexOf('/');
        if (slash >= 0) last = last[(slash + 1)..];
        var colon = last.LastIndexOf(':'); // "C:evil.exe" o flujos alternativos "a.txt:ads"
        if (colon >= 0) last = last[(colon + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(last.Length);
        foreach (var c in last)
        {
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0) continue;
            if (c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069') continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '.')) return fallback;

        var baseName = Path.GetFileNameWithoutExtension(clean);
        if (Reserved.Contains(baseName.Split('.')[0])) clean = "_" + clean;

        if (clean.Length > MaxLength)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 16) ext = "";
            clean = clean[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return clean;
    }

    /// <summary>Ruta libre en la carpeta: "informe.pdf", "informe (2).pdf", …</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>"2,4 MB", "830 KB", "12 bytes".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}

/// <summary>
/// Marca un archivo descargado como "venido de otra PC" (flujo Zone.Identifier, zona Internet):
/// Windows avisa antes de ejecutarlo y Office lo abre en vista protegida. Si el disco no lo
/// admite (FAT, USB), se ignora.
/// </summary>
public static class MarkOfTheWeb
{
    public static void Apply(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch
        {
            // sistema de archivos sin flujos alternativos: sin marca
        }
    }
}
