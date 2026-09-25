using System.Net;
using System.Security.Cryptography;
using System.Text;
using Susurro.Core.Config;
using Susurro.Core.Updates;

namespace Susurro.Core.Tests;

/// <summary>Actualización automática: manifiesto, descarga verificada, reemplazo del ejecutable y vuelta atrás.</summary>
public sealed class UpdateTests : IDisposable
{
    private const string ManifestUrl = "https://github.com/TintoUriel/Susurro/releases/latest/download/susurro-update.json";
    private const string ExeUrl = "https://github.com/TintoUriel/Susurro/releases/download/v2.9.0/Susurro.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "susurro-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly byte[] _oldBytes = Encoding.UTF8.GetBytes("versión vieja");
    private readonly byte[] _newBytes = RandomNumberGenerator.GetBytes(300_000);
    private readonly FakeHttp _http = new();

    public UpdateTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Susurro.exe");
        File.WriteAllBytes(_exe, _oldBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Updater NewUpdater(string current = "2.2.0", TimeSpan? firstCheck = null) => new(new UpdateOptions
    {
        CurrentVersion = Version.Parse(current),
        ExecutablePath = _exe,
        ManifestUrl = ManifestUrl,
        Handler = _http,
        FirstCheckMin = firstCheck ?? TimeSpan.FromMinutes(2),
        FirstCheckMax = firstCheck ?? TimeSpan.FromMinutes(10),
    });

    private void Publish(string version = "2.9.0", byte[]? bytes = null, string? sha = null, long? size = null, string url = ExeUrl)
    {
        bytes ??= _newBytes;
        sha ??= Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _http.Routes[ManifestUrl] = () => Json($$"""{ "version": "{{version}}", "url": "{{url}}", "sha256": "{{sha}}", "size": {{size ?? bytes.Length}} }""");
        _http.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private void AssertUntouched()
    {
        Assert.Equal(_oldBytes, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(_exe + Updater.DownloadSuffix));
        Assert.False(File.Exists(_exe + Updater.OldSuffix));
    }

    [Fact]
    public async Task Newer_version_is_downloaded_verified_and_replaces_the_executable()
    {
        Publish();
        using var updater = NewUpdater();
        Version? staged = null;
        updater.Staged += v => staged = v;

        var result = await updater.CheckNowAsync();

        Assert.Equal(UpdateOutcome.Staged, result.Outcome);
        Assert.Equal(new Version(2, 9, 0), result.Version);
        Assert.Equal(new Version(2, 9, 0), staged);
        Assert.Equal(new Version(2, 9, 0), updater.StagedVersion);
        Assert.Equal(_newBytes, File.ReadAllBytes(_exe));
        Assert.Equal(_oldBytes, File.ReadAllBytes(_exe + Updater.OldSuffix)); // para volver atrás si no arranca
        Assert.False(File.Exists(_exe + Updater.DownloadSuffix));

        // Ya quedó lista: no se vuelve a descargar.
        var downloads = _http.Requests.Count(u => u == ExeUrl);
        Assert.Equal(UpdateOutcome.Staged, (await updater.CheckNowAsync()).Outcome);
        Assert.Equal(downloads, _http.Requests.Count(u => u == ExeUrl));
    }

    [Theory]
    [InlineData("2.2.0")]
    [InlineData("2.1.9")]
    [InlineData("v2.2")]
    public async Task Same_or_older_version_is_ignored(string published)
    {
        Publish(version: published);
        using var updater = NewUpdater("2.2.0.0");
        var result = await updater.CheckNowAsync();
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.DoesNotContain(ExeUrl, _http.Requests);
        AssertUntouched();
    }

    [Fact]
    public async Task Hash_mismatch_leaves_everything_as_it_was()
    {
        Publish(sha: new string('a', 64));
        using var updater = NewUpdater();
        var result = await updater.CheckNowAsync();
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("SHA-256", result.Detail);
        Assert.Null(updater.StagedVersion);
        AssertUntouched();
    }

    [Fact]
    public async Task Download_bigger_or_smaller_than_announced_is_rejected()
    {
        // El servidor manda más bytes de los que dice el manifiesto (sin Content-Length confiable).
        Publish(size: _newBytes.Length - 10);
        _http.Routes[ExeUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(_newBytes)) };
        using (var updater = NewUpdater())
            Assert.Equal(UpdateOutcome.Failed, (await updater.CheckNowAsync()).Outcome);
        AssertUntouched();

        Publish(size: _newBytes.Length + 10);
        _http.Routes[ExeUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(_newBytes)) };
        using (var updater = NewUpdater())
            Assert.Equal(UpdateOutcome.Failed, (await updater.CheckNowAsync()).Outcome);
        AssertUntouched();
    }

    [Theory]
    [InlineData("http://github.com/TintoUriel/Susurro/releases/download/v2.9.0/Susurro.exe")]
    [InlineData("https://evil.example.com/Susurro.exe")]
    [InlineData("https://github.com.evil.example/Susurro.exe")]
    [InlineData("https://githubusercontent.com/Susurro.exe")]
    [InlineData("file:///C:/Windows/Susurro.exe")]
    public async Task Downloads_only_from_github_over_https(string url)
    {
        Publish(url: url);
        using var updater = NewUpdater();
        var result = await updater.CheckNowAsync();
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(url, _http.Requests);
        AssertUntouched();
    }

    [Fact]
    public void Github_asset_hosts_are_allowed()
    {
        Assert.True(Updater.IsAllowedHost("github.com", UpdateOptions.DefaultAllowedHosts));
        Assert.True(Updater.IsAllowedHost("objects.githubusercontent.com", UpdateOptions.DefaultAllowedHosts));
        Assert.True(Updater.IsAllowedHost("release-assets.githubusercontent.com", UpdateOptions.DefaultAllowedHosts));
        Assert.False(Updater.IsAllowedHost("githubusercontent.com.evil.example", UpdateOptions.DefaultAllowedHosts));
        Assert.False(Updater.IsAllowedHost("notgithub.com", UpdateOptions.DefaultAllowedHosts));
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("{}")]
    [InlineData("""{ "version": "dos", "url": "https://github.com/a", "sha256": "00", "size": 1 }""")]
    [InlineData("""{ "version": "2.9.0", "url": "https://github.com/a", "sha256": "xyz", "size": 1 }""")]
    [InlineData("""{ "version": "2.9.0", "url": "https://github.com/a", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "size": 0 }""")]
    [InlineData("""{ "version": "2.9.0", "url": "https://github.com/a", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "size": 999999999999 }""")]
    public async Task Invalid_manifests_are_rejected(string json)
    {
        _http.Routes[ManifestUrl] = () => Json(json);
        using var updater = NewUpdater();
        Assert.Equal(UpdateOutcome.Failed, (await updater.CheckNowAsync()).Outcome);
        AssertUntouched();
    }

    [Fact]
    public void Manifest_generated_by_the_ci_is_understood()
    {
        // Lo que escribe ConvertTo-Json en .github/workflows/ci.yml (con BOM por si se edita a mano).
        var json = "\uFEFF{\r\n  \"version\": \"2.9.0\",\r\n  \"url\": \"" + ExeUrl + "\",\r\n" +
                   "  \"sha256\": \"" + new string('0', 64) + "\",\r\n  \"size\": 146512345\r\n}";
        var m = Updater.ParseManifest(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(m);
        Assert.True(Updater.TryValidate(m, new UpdateOptions { CurrentVersion = new Version(2, 2, 0), ExecutablePath = _exe }, out var v, out var url, out _));
        Assert.Equal(new Version(2, 9, 0), v);
        Assert.Equal(ExeUrl, url!.ToString());
        Assert.Equal(146512345, m!.Size);
    }

    [Fact]
    public async Task Huge_manifest_is_rejected_before_reading_it_all()
    {
        _http.Routes[ManifestUrl] = () => Json(new string(' ', Updater.MaxManifestBytes + 100) + "{}");
        using var updater = NewUpdater();
        var result = await updater.CheckNowAsync();
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("grande", result.Detail);
    }

    [Fact]
    public async Task No_network_or_no_release_is_not_an_error_that_breaks_anything()
    {
        _http.Routes[ManifestUrl] = () => throw new HttpRequestException("sin conexión");
        using (var updater = NewUpdater())
            Assert.Equal(UpdateOutcome.Failed, (await updater.CheckNowAsync()).Outcome);

        _http.Routes[ManifestUrl] = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        using (var updater = NewUpdater())
            Assert.Equal(UpdateOutcome.Failed, (await updater.CheckNowAsync()).Outcome);
        AssertUntouched();
    }

    [Fact]
    public async Task Rollback_restores_the_previous_executable_and_the_version_is_not_retried()
    {
        Publish();
        using var updater = NewUpdater();
        Assert.Equal(UpdateOutcome.Staged, (await updater.CheckNowAsync()).Outcome);

        Assert.True(Updater.Rollback(_exe));
        Assert.Equal(_oldBytes, File.ReadAllBytes(_exe));
        Assert.Equal(_newBytes, File.ReadAllBytes(_exe + Updater.BadSuffix));
        Assert.False(File.Exists(_exe + Updater.OldSuffix));

        updater.Reject(new Version(2, 9, 0));
        Assert.Null(updater.StagedVersion);
        var again = await updater.CheckNowAsync();
        Assert.Equal(UpdateOutcome.Failed, again.Outcome);
        Assert.Equal(_oldBytes, File.ReadAllBytes(_exe));

        Updater.CleanupLeftovers(_exe);
        Assert.False(File.Exists(_exe + Updater.BadSuffix));
        Assert.False(Updater.Rollback(_exe)); // no hay a qué volver
    }

    [Fact]
    public async Task Leftovers_of_a_previous_update_are_cleaned_and_do_not_block_the_next_one()
    {
        File.WriteAllText(_exe + Updater.OldSuffix, "de antes");
        File.WriteAllText(_exe + Updater.DownloadSuffix, "a medias");
        Publish();
        using var updater = NewUpdater();
        Assert.Equal(UpdateOutcome.Staged, (await updater.CheckNowAsync()).Outcome);
        Assert.Equal(_oldBytes, File.ReadAllBytes(_exe + Updater.OldSuffix));

        Updater.CleanupLeftovers(_exe);
        Assert.False(File.Exists(_exe + Updater.OldSuffix));
        Assert.Equal(_newBytes, File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task Scheduled_check_runs_once_after_start_and_stops_after_staging()
    {
        Publish();
        using var updater = NewUpdater(firstCheck: TimeSpan.FromMilliseconds(30));
        var staged = new TaskCompletionSource<Version>(TaskCreationOptions.RunContinuationsAsynchronously);
        updater.Staged += v => staged.TrySetResult(v);

        updater.Start();
        var version = await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new Version(2, 9, 0), version);
        Assert.NotNull(updater.LastCheckUtc);
        Assert.Equal(1, _http.Requests.Count(u => u == ManifestUrl));
    }

    [Theory]
    [InlineData("2.2.0", 2, 2, 0)]
    [InlineData("v2.10.1", 2, 10, 1)]
    [InlineData("3.0", 3, 0, 0)]
    [InlineData("2.2.0.7", 2, 2, 0)]
    public void Versions_are_compared_by_three_components(string text, int major, int minor, int build)
    {
        Assert.True(Updater.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
        Assert.False(Updater.TryParseVersion("", out _));
        Assert.False(Updater.TryParseVersion("latest", out _));
    }

    [Fact]
    public void Auto_update_is_on_by_default_even_for_old_settings_files()
    {
        var dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "schemaVersion": 2, "friendlyName": "Ana", "setupCompleted": true, "startWithWindows": false }""");
        var s = new SettingsStore(dir).Load("PC", out var existed);
        Assert.True(existed);
        Assert.True(s.AutoUpdate);
    }

    /// <summary>Red falsa: responde según la URL pedida y anota cada pedido.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Log = new();
        public IEnumerable<string> Requests => Log;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Log.Enqueue(url);
            if (!Routes.TryGetValue(url, out var respond)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = respond();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
