using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Susurro.Core.Logging;
using Susurro.Core.Protocol;

namespace Susurro.Core.Updates;

/// <summary>Última versión publicada: <c>susurro-update.json</c>, adjunto a cada release de GitHub.</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    /// <summary>Dirección HTTPS de Susurro.exe.</summary>
    public string Url { get; set; } = "";
    /// <summary>SHA-256 de Susurro.exe en hexadecimal.</summary>
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public enum UpdateOutcome
{
    /// <summary>No hay una versión más nueva.</summary>
    UpToDate,
    /// <summary>La versión nueva ya reemplazó al ejecutable; se usa al reiniciar.</summary>
    Staged,
    /// <summary>La carpeta del programa no admite escritura (instalación vieja o sin permisos).</summary>
    NotWritable,
    /// <summary>Sin conexión, manifiesto inválido, descarga incompleta o hash distinto.</summary>
    Failed,
}

public sealed record UpdateResult(UpdateOutcome Outcome, Version? Version = null, string? Detail = null);

public sealed class UpdateOptions
{
    public const string DefaultManifestUrl = "https://github.com/TintoUriel/Susurro/releases/latest/download/susurro-update.json";

    /// <summary>Hosts de los que se acepta descargar (GitHub redirige los adjuntos a *.githubusercontent.com).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedHosts = new[] { "github.com", ".githubusercontent.com" };

    public required Version CurrentVersion { get; init; }
    /// <summary>Ejecutable en uso (Susurro.exe): se reemplaza por la versión nueva.</summary>
    public required string ExecutablePath { get; init; }
    public string ManifestUrl { get; init; } = DefaultManifestUrl;
    public IReadOnlyList<string> AllowedHosts { get; init; } = DefaultAllowedHosts;
    /// <summary>Solo para tests: reemplaza la red.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Primera búsqueda: al azar en este rango tras arrancar (no todas las PCs de la oficina a la vez).</summary>
    public TimeSpan FirstCheckMin { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan FirstCheckMax { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Una búsqueda por día. Es el único tráfico hacia afuera de la LAN.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public long MaxDownloadBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>
/// Actualización automática y silenciosa desde las releases de GitHub.
/// Una búsqueda al rato de arrancar y después una por día (temporizador de un disparo, no hay sondeo).
/// Si hay una versión más nueva: descarga Susurro.exe junto al actual (".download"), verifica tamaño y
/// SHA-256 contra el manifiesto (servido por HTTPS desde github.com) y recién ahí lo cambia de lugar:
/// el ejecutable en uso pasa a ".old" (Windows permite renombrar un .exe abierto) y el nuevo toma su
/// nombre. La App reinicia cuando no se está usando; si la versión nueva no arranca, <see cref="Rollback"/>.
/// </summary>
public sealed class Updater : IDisposable
{
    public const int MaxManifestBytes = 16 * 1024;
    public const string DownloadSuffix = ".download";
    public const string OldSuffix = ".old";
    public const string BadSuffix = ".bad";

    private readonly UpdateOptions _opts;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<Version> _rejected = new();
    private readonly object _gate = new();
    private Task<UpdateResult>? _running;
    private bool _scheduled;
    private bool _disposed;

    public Updater(UpdateOptions options)
    {
        _opts = options;
        CurrentVersion = Normalize(options.CurrentVersion);
        _timer = new Timer(_ => _ = RunScheduledAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>La versión nueva quedó lista en disco (se dispara en un hilo del pool).</summary>
    public event Action<Version>? Staged;

    public Version CurrentVersion { get; }
    public Version? StagedVersion { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }
    public UpdateResult? LastResult { get; private set; }

    // ------------------------------------------------------------------ programación

    /// <summary>Programa la primera búsqueda (al azar entre <see cref="UpdateOptions.FirstCheckMin"/> y Max).</summary>
    public void Start()
    {
        var min = _opts.FirstCheckMin.TotalMilliseconds;
        var max = Math.Max(min, _opts.FirstCheckMax.TotalMilliseconds);
        Schedule(TimeSpan.FromMilliseconds(min + Random.Shared.NextDouble() * (max - min)));
    }

    /// <summary>No se busca más hasta el próximo <see cref="Start"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _scheduled = false;
            if (_disposed) return;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Tras una suspensión: si pasó más de un día desde la última búsqueda, busca pronto.</summary>
    public void NotifyResumed()
    {
        bool due;
        lock (_gate) due = _scheduled && (LastCheckUtc == null || DateTime.UtcNow - LastCheckUtc >= _opts.CheckInterval);
        if (due) Start();
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed || StagedVersion != null) return;
            _scheduled = true;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunScheduledAsync()
    {
        await CheckNowAsync().ConfigureAwait(false);
        bool again;
        lock (_gate) again = _scheduled && StagedVersion == null;
        if (again) Schedule(_opts.CheckInterval);
    }

    /// <summary>La versión nueva no arrancó (se volvió a la anterior): no se la vuelve a instalar en esta sesión.</summary>
    public void Reject(Version version)
    {
        lock (_gate)
        {
            _rejected.Add(Normalize(version));
            StagedVersion = null;
        }
        if (_scheduled) Schedule(_opts.CheckInterval);
    }

    // ------------------------------------------------------------------ búsqueda y descarga

    /// <summary>Busca, descarga y deja lista la versión nueva. Nunca lanza excepciones.</summary>
    public Task<UpdateResult> CheckNowAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new UpdateResult(UpdateOutcome.Failed, null, "detenido"));
            if (_running is { IsCompleted: false }) return _running;
            return _running = Task.Run(RunCheckAsync);
        }
    }

    private async Task<UpdateResult> RunCheckAsync()
    {
        UpdateResult result;
        try
        {
            result = await CheckCoreAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            result = new UpdateResult(UpdateOutcome.Failed, null, "detenido");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Sin internet, proxy, antivirus con el archivo abierto…: se reintenta en la próxima búsqueda.
            result = new UpdateResult(UpdateOutcome.Failed, null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("update", "Error inesperado al buscar actualizaciones", ex);
            result = new UpdateResult(UpdateOutcome.Failed, null, ex.Message);
        }

        LastCheckUtc = DateTime.UtcNow;
        LastResult = result;
        switch (result.Outcome)
        {
            case UpdateOutcome.Staged:
                Log.Info("update", $"Versión {result.Version} descargada y verificada: se usa al reiniciar");
                break;
            case UpdateOutcome.UpToDate:
                Log.Info("update", $"Al día ({CurrentVersion.ToString(3)})");
                break;
            default:
                Log.Warn("update", $"No se pudo actualizar: {result.Detail}");
                break;
        }
        if (result.Outcome == UpdateOutcome.Staged && result.Version != null) Staged?.Invoke(result.Version);
        return result;
    }

    private async Task<UpdateResult> CheckCoreAsync(CancellationToken ct)
    {
        if (StagedVersion != null) return new UpdateResult(UpdateOutcome.Staged, StagedVersion);

        using var http = CreateClient();
        var manifest = await FetchManifestAsync(http, ct).ConfigureAwait(false);
        if (!TryValidate(manifest, _opts, out var version, out var url, out var error))
            return new UpdateResult(UpdateOutcome.Failed, null, "manifiesto inválido: " + error);
        if (version <= CurrentVersion) return new UpdateResult(UpdateOutcome.UpToDate, version);
        lock (_gate)
        {
            if (_rejected.Contains(version))
                return new UpdateResult(UpdateOutcome.Failed, version, "esa versión no arrancó en esta PC");
        }

        var exe = _opts.ExecutablePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
        if (!CanWriteTo(dir)) return new UpdateResult(UpdateOutcome.NotWritable, version, "la carpeta del programa no admite escritura");

        var download = exe + DownloadSuffix;
        try
        {
            await DownloadAsync(http, url!, download, manifest!.Size, manifest.Sha256, ct).ConfigureAwait(false);
            Stage(exe, download);
        }
        catch
        {
            TryDelete(download); // nunca queda un ejecutable a medias
            throw;
        }
        lock (_gate) StagedVersion = version;
        return new UpdateResult(UpdateOutcome.Staged, version);
    }

    private HttpClient CreateClient()
    {
        var client = _opts.Handler != null
            ? new HttpClient(_opts.Handler, disposeHandler: false)
            : new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                MaxAutomaticRedirections = 5,
                // Proxy de la oficina con autenticación de Windows (el proxy del sistema se usa solo).
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            });
        client.Timeout = Timeout.InfiniteTimeSpan; // cada paso tiene su propio límite
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Susurro/{CurrentVersion.ToString(3)}");
        return client;
    }

    private async Task<UpdateManifest?> FetchManifestAsync(HttpClient http, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opts.ManifestTimeout);
        using var response = await http.GetAsync(_opts.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("todavía no hay una release con susurro-update.json (404)");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new IOException("manifiesto demasiado grande");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var buffer = new byte[MaxManifestBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read > MaxManifestBytes) throw new IOException("manifiesto demasiado grande");
        return ParseManifest(buffer.AsSpan(0, read));
    }

    internal static UpdateManifest? ParseManifest(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..]; // BOM (si se editó a mano)
        try
        {
            return JsonSerializer.Deserialize(utf8, SusurroJson.Update.UpdateManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool TryValidate(UpdateManifest? m, UpdateOptions opts, out Version version, out Uri? url, out string? error)
    {
        version = new Version(0, 0, 0);
        url = null;
        error = null;
        if (m == null) { error = "vacío o no es JSON"; return false; }
        if (!TryParseVersion(m.Version, out version)) { error = "versión"; return false; }
        if (m.Sha256 is not { Length: 64 } || !m.Sha256.All(Uri.IsHexDigit)) { error = "sha256"; return false; }
        if (m.Size <= 0 || m.Size > opts.MaxDownloadBytes) { error = "tamaño"; return false; }
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(url.Host, opts.AllowedHosts))
        {
            url = null;
            error = "dirección de descarga no permitida";
            return false;
        }
        return true;
    }

    /// <summary>"github.com" exacto, o ".githubusercontent.com" como sufijo de dominio.</summary>
    internal static bool IsAllowedHost(string host, IEnumerable<string> allowed) =>
        allowed.Any(a => a.StartsWith('.')
            ? host.EndsWith(a, StringComparison.OrdinalIgnoreCase) && host.Length > a.Length
            : string.Equals(host, a, StringComparison.OrdinalIgnoreCase));

    private async Task DownloadAsync(HttpClient http, Uri url, string target, long size, string sha256, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opts.DownloadTimeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared && declared != size)
            throw new IOException($"el servidor anuncia {declared} bytes y el manifiesto {size}");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > size) throw new IOException("la descarga es más grande que lo anunciado");
                hash.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
            }
            if (total != size) throw new IOException($"descarga incompleta ({total} de {size} bytes)");
            await file.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("el SHA-256 de la descarga no coincide con el manifiesto");
    }

    // ------------------------------------------------------------------ archivos

    /// <summary>El ejecutable en uso pasa a ".old" y la descarga verificada toma su lugar.</summary>
    internal static void Stage(string exe, string download)
    {
        var old = exe + OldSuffix;
        TryDelete(old);
        if (File.Exists(old)) throw new IOException("no se pudo borrar la copia anterior (" + Path.GetFileName(old) + ")");
        File.Move(exe, old);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe); // se deja todo como estaba
            throw;
        }
    }

    /// <summary>La versión nueva no arrancó: se vuelve a la anterior (la nueva queda como ".bad").</summary>
    public static bool Rollback(string exe)
    {
        var old = exe + OldSuffix;
        if (!File.Exists(old)) return false;
        try
        {
            var bad = exe + BadSuffix;
            TryDelete(bad);
            if (File.Exists(exe)) File.Move(exe, bad);
            File.Move(old, exe);
            Log.Warn("update", "La versión nueva no arrancó: se volvió a la anterior");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update", "No se pudo volver a la versión anterior", ex);
            return false;
        }
    }

    /// <summary>Al arrancar: borra lo que haya quedado de una actualización anterior.</summary>
    public static void CleanupLeftovers(string exe)
    {
        foreach (var suffix in new[] { OldSuffix, DownloadSuffix, BadSuffix })
            TryDelete(exe + suffix);
    }

    internal static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".susurro-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // en uso (otra instancia con la versión vieja) o sin permiso: se intenta en el próximo arranque
        }
    }

    // ------------------------------------------------------------------ versiones

    /// <summary>"2.2.0", "v2.2.0", "2.2" → 2.2.0 (siempre tres componentes).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        if (!System.Version.TryParse(t, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _scheduled = false;
        }
        try { _cts.Cancel(); } catch { }
        _timer.Dispose();
    }
}
