using System.Text;

namespace Susurro.Core.Logging;

public enum LogLevel { Info, Warn, Error }

public interface ILogSink
{
    void Write(LogLevel level, string category, string message, Exception? exception);
}

/// <summary>
/// Fachada estática de logging. Por defecto no escribe nada (útil en tests);
/// la aplicación la inicializa con un <see cref="FileLogSink"/>.
/// </summary>
public static class Log
{
    private static volatile ILogSink? _sink;

    public static void Initialize(ILogSink? sink) => _sink = sink;

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message, Exception? ex = null) => Write(LogLevel.Warn, category, message, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        try { _sink?.Write(level, category, message, ex); }
        catch { /* el logging nunca debe tumbar la aplicación */ }
    }
}

/// <summary>
/// Log en archivo de texto con límite de tamaño y rotación simple:
/// susurro.log (actual) + susurro.1.log (anterior). Tamaño máximo total ≈ 2 × maxBytes.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly long _maxBytes;

    public FileLogSink(string directory, string fileName = "susurro.log", long maxBytes = 256 * 1024)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, fileName);
        PreviousFilePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".1" + Path.GetExtension(fileName));
        _maxBytes = Math.Max(16 * 1024, maxBytes);
    }

    public string FilePath { get; }
    public string PreviousFilePath { get; }

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder(160);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(level switch { LogLevel.Warn => " WARN ", LogLevel.Error => " ERROR", _ => " INFO " });
        sb.Append(" [").Append(category).Append("] ").Append(message);
        if (exception != null)
        {
            sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            // Para errores, el primer marco de la pila (dónde ocurrió) sin volcar la traza completa.
            if (level == LogLevel.Error && exception.StackTrace is { } st)
            {
                var first = st.Split('\n', 2)[0].Trim();
                if (first.Length > 0) sb.Append(" @ ").Append(first.Length > 200 ? first[..200] : first);
            }
        }
        sb.AppendLine();

        lock (_gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > _maxBytes)
                    File.Move(FilePath, PreviousFilePath, overwrite: true);
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // disco lleno, archivo bloqueado, etc.: se descarta la línea.
            }
        }
    }

    /// <summary>Devuelve el contenido del log (anterior + actual) para el visor de diagnóstico.</summary>
    public string ReadAll()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            try
            {
                if (File.Exists(PreviousFilePath)) sb.Append(File.ReadAllText(PreviousFilePath));
                if (File.Exists(FilePath)) sb.Append(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                sb.AppendLine("No se pudo leer el registro: " + ex.Message);
            }
            return sb.ToString();
        }
    }
}
