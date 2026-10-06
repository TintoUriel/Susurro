using System.Net;
using System.Security.Cryptography;
using Susurro.Core.Logging;
using Susurro.Core.Updates;

namespace Susurro.Core.RemoteSupport;

/// <summary>
/// Una versión de RustDesk fijada: de dónde se baja y cómo se verifica. No se usa «latest»: se fija una
/// versión <b>auditada</b>, con su SHA-256 tomado del binario que la persona revisó. Así una release nueva
/// de RustDesk (o una comprometida) no llega sola a las PCs de la oficina —el mismo recaudo que con la
/// actualización de Susurro—.
/// </summary>
public sealed record RustDeskRelease
{
    /// <summary>Versión de RustDesk que se fija (solo informativo / nombre del archivo en caché).</summary>
    public required string Version { get; init; }
    /// <summary>Dirección HTTPS del .exe de RustDesk (debe ser de github.com / *.githubusercontent.com).</summary>
    public required string Url { get; init; }
    /// <summary>SHA-256 del .exe en hexadecimal (64 dígitos). Vacío = sin fijar: no se descarga nada.</summary>
    public required string Sha256 { get; init; }
    /// <summary>Tamaño exacto del .exe en bytes. 0 = sin fijar.</summary>
    public required long Size { get; init; }

    /// <summary>true si <see cref="Sha256"/> y <see cref="Size"/> están completos (se puede verificar la descarga).</summary>
    public bool IsPinned => Size > 0 && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}

public sealed class RustDeskOptions
{
    /// <summary>La versión fijada que se va a descargar y verificar.</summary>
    public required RustDeskRelease Release { get; init; }
    /// <summary>Carpeta donde queda el .exe en caché (p. ej. %LOCALAPPDATA%\Susurro\tools).</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Hosts aceptados para descargar (los mismos que la actualización: github.com / *.githubusercontent.com).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = UpdateOptions.DefaultAllowedHosts;
    /// <summary>Solo para tests: reemplaza la red.</summary>
    public HttpMessageHandler? Handler { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxDownloadBytes { get; init; } = 256L * 1024 * 1024;
    public string UserAgent { get; init; } = "Susurro";
}

public enum RustDeskOutcome
{
    /// <summary>El .exe está en caché, verificado, listo para abrir.</summary>
    Ready,
    /// <summary>La versión no está fijada (falta SHA-256 / tamaño): no se descarga nada.</summary>
    NotPinned,
    /// <summary>Sin conexión, descarga incompleta, hash distinto o dirección no permitida.</summary>
    Failed,
}

public sealed record RustDeskResult(RustDeskOutcome Outcome, string? Path = null, string? Detail = null);

/// <summary>
/// Trae RustDesk <b>a pedido</b> (la primera vez que se usa <c>/control</c>), no viene adentro de Susurro ni
/// se reparte por la actualización automática: solo lo baja la PC que da soporte y cuando la persona lo pide.
/// <para>
/// Reusa el mismo recaudo que <see cref="Updater"/>: descarga por HTTPS solo desde github.com /
/// *.githubusercontent.com, a un archivo temporal, verifica tamaño y SHA-256 contra la versión fijada y recién
/// ahí lo mueve a su lugar. Nunca deja un ejecutable a medias. Antes de devolver el que ya está en caché lo
/// vuelve a verificar: si no coincide, lo baja de nuevo. Sin costo en reposo: no hay temporizadores ni sondeo.
/// </para>
/// Susurro no captura pantallas ni inyecta teclado/mouse: solo deja listo el binario de RustDesk, que maneja
/// la conexión y pide permiso del lado de la otra persona.
/// </summary>
public sealed class RustDeskProvisioner
{
    public const string PartSuffix = ".part";

    private readonly RustDeskOptions _opts;
    private readonly SemaphoreSlim _gate = new(1, 1); // una descarga por vez

    public RustDeskProvisioner(RustDeskOptions options) => _opts = options;

    /// <summary>Ruta del .exe en caché (exista o no todavía).</summary>
    public string ExecutablePath => Path.Combine(_opts.CacheDirectory, $"rustdesk-{_opts.Release.Version}.exe");

    /// <summary>Deja listo (o confirma) el RustDesk verificado y devuelve su ruta. Nunca lanza excepciones.</summary>
    public async Task<RustDeskResult> EnsureAsync(CancellationToken ct = default)
    {
        var release = _opts.Release;
        if (!release.IsPinned)
        {
            Log.Warn("support", "RustDesk no está fijado (falta SHA-256 o tamaño): no se descarga nada");
            return new RustDeskResult(RustDeskOutcome.NotPinned, null, "RustDesk no está fijado");
        }

        var exe = ExecutablePath;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RustDeskResult(RustDeskOutcome.Failed, null, "cancelado");
        }
        try
        {
            // Ya en caché y verificado → listo (sin red).
            if (File.Exists(exe) && await MatchesAsync(exe, release.Size, release.Sha256, ct).ConfigureAwait(false))
                return new RustDeskResult(RustDeskOutcome.Ready, exe);

            return await DownloadAsync(release, exe, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RustDeskResult(RustDeskOutcome.Failed, null, "cancelado");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Warn("support", "No se pudo preparar RustDesk", ex);
            return new RustDeskResult(RustDeskOutcome.Failed, null, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RustDeskResult> DownloadAsync(RustDeskRelease release, string exe, CancellationToken ct)
    {
        if (!Updater.TryValidate(ToManifest(release), ToUpdateOptions(), out _, out var url, out var error))
            return new RustDeskResult(RustDeskOutcome.Failed, null, "versión fijada inválida: " + error);

        Directory.CreateDirectory(_opts.CacheDirectory);
        var part = exe + PartSuffix;
        try
        {
            using var http = CreateClient();
            await FetchAsync(http, url!, part, release.Size, release.Sha256, ct).ConfigureAwait(false);
            File.Move(part, exe, overwrite: true); // atómico en el mismo volumen
        }
        catch
        {
            TryDelete(part); // nunca queda un ejecutable a medias
            throw;
        }
        Log.Info("support", $"RustDesk {release.Version} descargado y verificado");
        return new RustDeskResult(RustDeskOutcome.Ready, exe);
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
                DefaultProxyCredentials = CredentialCache.DefaultCredentials, // proxy de la oficina
            });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_opts.UserAgent);
        return client;
    }

    /// <summary>Descarga a <paramref name="target"/> verificando tamaño y SHA-256; tira si algo no cuadra.</summary>
    private async Task FetchAsync(HttpClient http, Uri url, string target, long size, string sha256, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opts.Timeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared && declared != size)
            throw new IOException($"el servidor anuncia {declared} bytes y la versión fijada {size}");

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
            throw new IOException("el SHA-256 de la descarga no coincide con la versión fijada");
    }

    /// <summary>¿El archivo en disco tiene el tamaño y el SHA-256 esperados? (verificar antes de abrirlo).</summary>
    private static async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(actual), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Reusa la validación de direcciones del Updater (HTTPS + host permitido + tamaño): misma lógica, mismos tests.
    private static UpdateManifest ToManifest(RustDeskRelease r) => new() { Version = r.Version, Url = r.Url, Sha256 = r.Sha256, Size = r.Size };
    private UpdateOptions ToUpdateOptions() => new()
    {
        CurrentVersion = new Version(0, 0, 0),
        ExecutablePath = ExecutablePath,
        AllowedHosts = _opts.AllowedHosts,
        MaxDownloadBytes = _opts.MaxDownloadBytes,
    };
}
