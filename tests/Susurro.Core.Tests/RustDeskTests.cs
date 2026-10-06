using System.Net;
using System.Security.Cryptography;
using Susurro.Core.RemoteSupport;

namespace Susurro.Core.Tests;

/// <summary>
/// RustDesk a pedido: descarga verificada desde GitHub, caché que no se vuelve a bajar, y rechazo de todo lo
/// que no cuadre (hash, tamaño, dirección, versión sin fijar). Susurro solo deja listo el binario.
/// </summary>
public sealed class RustDeskTests : IDisposable
{
    private const string ExeUrl = "https://github.com/rustdesk/rustdesk/releases/download/1.5.0/rustdesk-1.5.0-x86_64.exe";

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "susurro-rustdesk-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _goodBytes = RandomNumberGenerator.GetBytes(300_000);
    private readonly FakeHttp _http = new();

    public RustDeskTests() => Directory.CreateDirectory(_cache);

    public void Dispose()
    {
        try { Directory.Delete(_cache, true); } catch { }
    }

    private RustDeskProvisioner NewProvisioner(RustDeskRelease? release = null) => new(new RustDeskOptions
    {
        Release = release ?? Pinned(_goodBytes),
        CacheDirectory = _cache,
        Handler = _http,
    });

    private RustDeskRelease Pinned(byte[] bytes, string? sha = null, long? size = null, string url = ExeUrl, string version = "1.5.0") => new()
    {
        Version = version,
        Url = url,
        Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        Size = size ?? bytes.Length,
    };

    private void Serve(byte[]? bytes = null, string url = ExeUrl) =>
        _http.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? _goodBytes) };

    private string ExpectedExe => Path.Combine(_cache, "rustdesk-1.5.0.exe");

    private void AssertNoLeftovers()
    {
        Assert.False(File.Exists(ExpectedExe + RustDeskProvisioner.PartSuffix));
    }

    [Fact]
    public async Task First_use_downloads_verifies_and_caches_it()
    {
        Serve();
        var provisioner = NewProvisioner();

        var result = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.Ready, result.Outcome);
        Assert.Equal(ExpectedExe, result.Path);
        Assert.Equal(_goodBytes, File.ReadAllBytes(ExpectedExe));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Already_in_cache_is_not_downloaded_again()
    {
        Serve();
        var provisioner = NewProvisioner();
        Assert.Equal(RustDeskOutcome.Ready, (await provisioner.EnsureAsync()).Outcome);
        var downloads = _http.Requests.Count(u => u == ExeUrl);

        var again = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.Ready, again.Outcome);
        Assert.Equal(downloads, _http.Requests.Count(u => u == ExeUrl)); // no bajó de nuevo
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_leaves_nothing_behind()
    {
        Serve(); // los bytes servidos no coinciden con el SHA fijado
        var provisioner = NewProvisioner(Pinned(_goodBytes, sha: new string('a', 64)));

        var result = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.Failed, result.Outcome);
        Assert.Contains("SHA-256", result.Detail);
        Assert.False(File.Exists(ExpectedExe));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Size_mismatch_is_rejected()
    {
        // Content-Length real (ByteArrayContent) distinto del tamaño fijado.
        Serve();
        Assert.Equal(RustDeskOutcome.Failed, (await NewProvisioner(Pinned(_goodBytes, size: _goodBytes.Length - 1)).EnsureAsync()).Outcome);
        Assert.False(File.Exists(ExpectedExe));

        // Sin Content-Length confiable: el stream trae más bytes de los anunciados.
        _http.Routes[ExeUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(_goodBytes)) };
        Assert.Equal(RustDeskOutcome.Failed, (await NewProvisioner(Pinned(_goodBytes, size: _goodBytes.Length - 10)).EnsureAsync()).Outcome);
        Assert.False(File.Exists(ExpectedExe));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task A_corrupt_cached_file_is_verified_and_re_downloaded()
    {
        // Mismo tamaño, contenido distinto: no debe confiar en el que está en caché.
        File.WriteAllBytes(ExpectedExe, new byte[_goodBytes.Length]);
        Serve();
        var provisioner = NewProvisioner();

        var result = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.Ready, result.Outcome);
        Assert.Equal(_goodBytes, File.ReadAllBytes(ExpectedExe));
        Assert.Contains(ExeUrl, _http.Requests);
    }

    [Theory]
    [InlineData("http://github.com/rustdesk/rustdesk/releases/download/1.5.0/rustdesk.exe")]
    [InlineData("https://evil.example.com/rustdesk.exe")]
    [InlineData("https://github.com.evil.example/rustdesk.exe")]
    [InlineData("file:///C:/Windows/rustdesk.exe")]
    public async Task Downloads_only_from_github_over_https(string url)
    {
        Serve(url: url);
        var provisioner = NewProvisioner(Pinned(_goodBytes, url: url));

        var result = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(url, _http.Requests);
        Assert.False(File.Exists(ExpectedExe));
    }

    [Theory]
    [InlineData("", 100)]            // sin SHA
    [InlineData("abc", 100)]         // SHA incompleto
    [InlineData("", 0)]              // sin nada
    public async Task Not_pinned_downloads_nothing(string sha, long size)
    {
        var release = new RustDeskRelease { Version = "1.5.0", Url = ExeUrl, Sha256 = sha, Size = size };
        var provisioner = NewProvisioner(release);

        var result = await provisioner.EnsureAsync();

        Assert.Equal(RustDeskOutcome.NotPinned, result.Outcome);
        Assert.Empty(_http.Requests);
        Assert.False(File.Exists(ExpectedExe));
    }

    [Fact]
    public async Task No_network_is_a_clean_failure_not_a_crash()
    {
        _http.Routes[ExeUrl] = () => throw new HttpRequestException("sin conexión");
        var result = await NewProvisioner().EnsureAsync();
        Assert.Equal(RustDeskOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(ExpectedExe));
        AssertNoLeftovers();
    }

    [Fact]
    public void Executable_path_is_named_after_the_pinned_version()
    {
        Assert.Equal(ExpectedExe, NewProvisioner().ExecutablePath);
        Assert.EndsWith("rustdesk-1.5.0.exe", NewProvisioner().ExecutablePath);
    }

    /// <summary>Red falsa: responde según la URL pedida y anota cada pedido (igual que en UpdateTests).</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();
        public IEnumerable<string> Requests => _log;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            _log.Enqueue(url);
            if (!Routes.TryGetValue(url, out var respond)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = respond();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
