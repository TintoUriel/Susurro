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
    public void First_load_creates_defaults_without_using_the_machine_name()
    {
        var store = new SettingsStore(_dir);
        var s = store.Load("OFICINA-PC", out var existed);
        Assert.False(existed);
        Assert.True(SettingsValidator.IsValidInstanceId(s.InstanceId));
        Assert.Equal("", s.FriendlyName); // se pide el nombre de la persona en la bienvenida
        Assert.False(s.SetupCompleted);
        Assert.Equal(AppSettings.DefaultPort, s.Port);
        Assert.Empty(s.Contacts);
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
        s.SetupCompleted = true;
        s.LastRecipient = AppSettings.AllRecipients;
        s.Contacts.Add(new ContactSettings { InstanceId = SettingsValidator.NewInstanceId(), Name = "Oficina", LastAddress = "10.0.0.5", LastPort = 47810, Blocked = true });
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
        Assert.True(back.SetupCompleted);
        Assert.Equal(AppSettings.AllRecipients, back.LastRecipient);
        var c = Assert.Single(back.Contacts);
        Assert.Equal("Oficina", c.Name);
        Assert.Equal("10.0.0.5", c.LastAddress);
        Assert.True(c.Blocked);
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
    public void Invalid_and_repeated_contacts_are_removed()
    {
        var s = new AppSettings { InstanceId = SettingsValidator.NewInstanceId() };
        var other = SettingsValidator.NewInstanceId();
        s.Contacts.Add(new ContactSettings { InstanceId = s.InstanceId, Name = "Yo" });           // esta misma instancia
        s.Contacts.Add(new ContactSettings { InstanceId = "no-valido", Name = "X" });
        s.Contacts.Add(new ContactSettings { InstanceId = other, Name = "  " });
        s.Contacts.Add(new ContactSettings { InstanceId = other.ToUpperInvariant(), Name = "Repetido" });
        s.Contacts.Add(null!);
        var c = Assert.Single(SettingsValidator.Normalize(s, "PC").Contacts);
        Assert.Equal(other, c.InstanceId);
        Assert.Equal("Sin nombre", c.Name);
    }

    [Fact]
    public void Contact_list_is_capped_keeping_blocked_and_recent()
    {
        var list = Enumerable.Range(0, SettingsValidator.MaxContacts + 20)
            .Select(i => new ContactSettings { InstanceId = SettingsValidator.NewInstanceId(), Name = "P" + i, LastSeenUtc = DateTime.UtcNow.AddMinutes(i) })
            .ToList();
        list[0].Blocked = true; // el más viejo, pero bloqueado: tiene que seguir bloqueado
        var result = SettingsValidator.NormalizeContacts(list, SettingsValidator.NewInstanceId());
        Assert.Equal(SettingsValidator.MaxContacts, result.Count);
        Assert.Contains(result, c => c.Name == "P0");
        Assert.DoesNotContain(result, c => c.Name == "P1");
    }

    [Fact]
    public void Version_1_settings_ask_for_the_name_again()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(_dir);
        File.WriteAllText(store.FilePath,
            "{ \"schemaVersion\": 1, \"friendlyName\": \"OFICINA-PC\", \"setupCompleted\": true, " +
            "\"peer\": { \"instanceId\": \"0123456789abcdef0123456789abcdef\", \"name\": \"Otra\", \"protectedKey\": \"abc\" } }");
        var s = store.Load("OFICINA-PC", out _);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
        Assert.Equal("", s.FriendlyName);       // era el nombre del equipo: se descarta
        Assert.False(s.SetupCompleted);
        Assert.Empty(s.Contacts);

        File.WriteAllText(store.FilePath, "{ \"schemaVersion\": 1, \"friendlyName\": \"Tinto\", \"setupCompleted\": true }");
        s = store.Load("OFICINA-PC", out _);
        Assert.Equal("Tinto", s.FriendlyName);  // un nombre elegido se ofrece de nuevo
        Assert.False(s.SetupCompleted);
    }

    [Fact]
    public void Send_hotkey_defaults_and_can_be_disabled()
    {
        Assert.Equal("Ctrl+Shift+Space", new AppSettings().SendHotkey);

        // Configuración de una versión anterior (sin el campo) → atajo por defecto.
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(_dir);
        File.WriteAllText(store.FilePath, "{ \"friendlyName\": \"Tinto\" }");
        Assert.Equal(AppSettings.DefaultHotkey, store.Load("PC", out _).SendHotkey);

        // Desactivado a propósito ("") se respeta.
        var s = store.Load("PC", out _);
        s.SendHotkey = "";
        store.Save(s);
        Assert.Equal("", store.Load("PC", out _).SendHotkey);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var s = new AppSettings { Contacts = { new ContactSettings { Name = "A" } } };
        var c = s.Clone();
        c.Contacts[0].Name = "B";
        c.Contacts.Add(new ContactSettings());
        c.Overlay.FontSize = 40;
        Assert.Equal("A", s.Contacts[0].Name);
        Assert.Single(s.Contacts);
        Assert.NotEqual(40, s.Overlay.FontSize);
    }
}
