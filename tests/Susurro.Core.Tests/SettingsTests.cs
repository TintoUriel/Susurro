using Susurro.Core.Config;

namespace Susurro.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "susurro-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void First_load_creates_defaults_with_instance_id_and_machine_name()
    {
        var store = new SettingsStore(_dir);
        var s = store.Load("OFICINA-PC", out var existed);
        Assert.False(existed);
        Assert.True(SettingsValidator.IsValidInstanceId(s.InstanceId));
        Assert.Equal("OFICINA-PC", s.FriendlyName);
        Assert.Equal(AppSettings.DefaultPort, s.Port);
        Assert.Null(s.Peer);
        Assert.True(s.StartWithWindows); // inicio con Windows activado por defecto
    }

    [Fact]
    public void Save_and_load_roundtrip()
    {
        var store = new SettingsStore(_dir);
        var s = store.Load("PC", out _);
        s.FriendlyName = "Tinto";
        s.Port = 50000;
        s.Overlay.Position = OverlayPosition.TopRight;
        s.Overlay.DurationSeconds = 8;
        s.Overlay.ShowBackground = false;
        s.Overlay.TextOutline = true;
        s.Overlay.TextColor = SubtitleColor.Yellow;
        s.Peer = new PeerSettings { InstanceId = SettingsValidator.NewInstanceId(), Name = "Oficina", ProtectedKey = "abc", LastAddress = "10.0.0.5", LastPort = 47810 };
        Assert.True(store.Save(s));

        var back = store.Load("PC", out var existed);
        Assert.True(existed);
        Assert.Equal(s.InstanceId, back.InstanceId);
        Assert.Equal("Tinto", back.FriendlyName);
        Assert.Equal(50000, back.Port);
        Assert.Equal(OverlayPosition.TopRight, back.Overlay.Position);
        Assert.Equal(8, back.Overlay.DurationSeconds);
        Assert.False(back.Overlay.ShowBackground);
        Assert.True(back.Overlay.TextOutline);
        Assert.Equal(SubtitleColor.Yellow, back.Overlay.TextColor);
        Assert.Equal("Oficina", back.Peer!.Name);
        Assert.Equal("10.0.0.5", back.Peer.LastAddress);
        Assert.Contains("\"topRight\"", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_backed_up()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(_dir);
        File.WriteAllText(store.FilePath, "{ esto no es json");
        var s = store.Load("PC", out var existed);
        Assert.True(existed);
        Assert.NotNull(s);
        Assert.True(File.Exists(store.FilePath + ".corrupt"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = new AppSettings
        {
            InstanceId = "no-valido",
            Port = 80,
            DiscoveryPort = 80,
            FriendlyName = new string('x', 100),
            Overlay = new OverlaySettings { FontSize = 999, Opacity = -3, DurationSeconds = 0, MaxWidthPercent = 500, Position = (OverlayPosition)99 },
        };
        s = SettingsValidator.Normalize(s, "PC");
        Assert.True(SettingsValidator.IsValidInstanceId(s.InstanceId));
        Assert.Equal(AppSettings.DefaultPort, s.Port);
        Assert.NotEqual(s.Port, s.DiscoveryPort);
        Assert.Equal(SettingsValidator.MaxNameLength, s.FriendlyName.Length);
        Assert.Equal(48, s.Overlay.FontSize);
        Assert.Equal(0.3, s.Overlay.Opacity);
        Assert.Equal(1, s.Overlay.DurationSeconds);
        Assert.Equal(95, s.Overlay.MaxWidthPercent);
        Assert.Equal(OverlayPosition.BottomCenter, s.Overlay.Position);
    }

    [Fact]
    public void Invalid_peer_is_removed()
    {
        var s = new AppSettings { InstanceId = SettingsValidator.NewInstanceId() };
        s.Peer = new PeerSettings { InstanceId = s.InstanceId, ProtectedKey = "x" }; // se vinculó consigo misma
        Assert.Null(SettingsValidator.Normalize(s, "PC").Peer);

        s.Peer = new PeerSettings { InstanceId = SettingsValidator.NewInstanceId(), ProtectedKey = "" };
        Assert.Null(SettingsValidator.Normalize(s, "PC").Peer);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var s = new AppSettings { Peer = new PeerSettings { Name = "A" } };
        var c = s.Clone();
        c.Peer!.Name = "B";
        c.Overlay.FontSize = 40;
        Assert.Equal("A", s.Peer.Name);
        Assert.NotEqual(40, s.Overlay.FontSize);
    }
}
