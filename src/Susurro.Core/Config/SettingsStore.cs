using System.Text.Json;
using Susurro.Core.Logging;
using Susurro.Core.Protocol;

namespace Susurro.Core.Config;

/// <summary>
/// Persistencia de la configuración en un único JSON pequeño.
/// Escritura atómica (archivo temporal + reemplazo) para no corromperlo ante un corte de luz.
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();

    public SettingsStore(string directory)
    {
        DirectoryPath = directory;
        FilePath = Path.Combine(directory, "settings.json");
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }

    /// <summary>Carga la configuración. Nunca lanza: ante cualquier error devuelve valores por defecto.</summary>
    public AppSettings Load(string machineName, out bool existed)
    {
        existed = false;
        AppSettings? loaded = null;
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    existed = true;
                    var json = File.ReadAllText(FilePath);
                    loaded = JsonSerializer.Deserialize(json, SusurroJson.Settings.AppSettings);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("config", "Configuración ilegible; se usan valores por defecto (copia en settings.json.corrupt)", ex);
                try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
                loaded = null;
            }
        }
        return SettingsValidator.Normalize(loaded, machineName);
    }

    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var json = JsonSerializer.Serialize(settings, SusurroJson.Settings.AppSettings);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("config", "No se pudo guardar la configuración", ex);
                return false;
            }
        }
    }
}
