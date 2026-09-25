using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Susurro.App.Overlay;
using Susurro.App.Services;
using Susurro.App.Tray;
using Susurro.App.Views;
using Susurro.Core.Config;
using Susurro.Core.Identity;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;
using Susurro.Core.Net;

namespace Susurro.App;

/// <summary>Resultado de enviar a una persona o a todos los conectados.</summary>
internal sealed record SendOutcome(bool Accepted, IReadOnlyList<string> MessageIds, bool AnyOnline, string? Error)
{
    public static SendOutcome Fail(string error) => new(false, Array.Empty<string>(), false, error);
}

/// <summary>
/// Punto central de la aplicación: conecta configuración, red, overlay, bandeja y ventanas.
/// Todos los eventos de red se pasan al hilo de UI aquí; las vistas solo hablan con este controlador.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SingleInstance _instance;
    private readonly DispatcherTimer _trimTimer;
    private SettingsStore _store = null!;
    private LocalIdentity _identity = null!;
    private OverlayController _overlay = null!;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private IntPtr _quickReturnTo;
    private bool _quickMode;
    private MainWindow _main = null!;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private LogWindow? _logWindow;
    private bool _exiting;
    private bool _disposed;

    public AppController(CommandLine args, SingleInstance instance, Dispatcher ui)
    {
        Args = args;
        _instance = instance;
        _ui = ui;
        _trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (!AnyWindowVisible()) MemoryTrimmer.Trim();
        };
    }

    public CommandLine Args { get; }
    public AppSettings Settings { get; private set; } = null!;
    public PeerLink Link { get; private set; } = null!;
    public FileLogSink LogSink { get; private set; } = null!;
    public string DataDirectory => AppPaths.DataDirectory(Args.Profile);
    public bool HasTray => _tray != null;
    public bool IsExiting => _exiting;
    /// <summary>Ya se eligió el nombre y la comunicación está en marcha.</summary>
    public bool IsReady => Settings.SetupCompleted && Settings.FriendlyName.Length > 0;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public event Action<LinkState>? LinkStateChanged;
    public event Action<string, DeliveryState>? DeliveryChanged;
    /// <summary>Cambió la lista de personas o el estado de alguna.</summary>
    public event Action? ContactsChanged;
    /// <summary>Se completó la bienvenida (nombre elegido) o cambió el nombre.</summary>
    public event Action? SetupChanged;
    public event Action? HotkeyChanged;

    /// <summary>Atajo global configurado (null = desactivado o inválido).</summary>
    public HotkeyGesture? Hotkey => _hotkey?.Current;
    /// <summary>true si el atajo está registrado y funcionando.</summary>
    public bool HotkeyActive => _hotkey?.IsActive == true;

    // ------------------------------------------------------------------ arranque

    public void Start()
    {
        LogSink = new FileLogSink(AppPaths.LogDirectory(Args.Profile));
        Log.Initialize(LogSink);

        _store = new SettingsStore(DataDirectory);
        Settings = _store.Load(Environment.MachineName, out var existed);
        _identity = LocalIdentity.LoadOrCreate(Settings.IdentityKey, new DpapiKeyProtector(), out var protectedKey);
        var changed = !existed || protectedKey != Settings.IdentityKey || _identity.Id != Settings.InstanceId;
        Settings.IdentityKey = protectedKey;
        Settings.InstanceId = _identity.Id;
        // Primera ejecución: inicio con Windows activado (se desactiva en Configuración → General).
        // Los perfiles de desarrollo (--profile) no se agregan al inicio de Windows.
        if (!existed && Args.Profile != null) Settings.StartWithWindows = false;
        if (Args.Port is int port && port != Settings.Port) { Settings.Port = port; changed = true; }
        if (Args.DiscoveryPort is int dport && dport != Settings.DiscoveryPort) { Settings.DiscoveryPort = dport; changed = true; }
        Settings = SettingsValidator.Normalize(Settings, Environment.MachineName);
        if (changed) Save();

        Log.Info("app", $"Inicio de Susurro {Version}{(Args.Profile != null ? $" (perfil {Args.Profile})" : "")} — " +
                        $"«{(Settings.FriendlyName.Length > 0 ? Settings.FriendlyName : "sin nombre")}», " +
                        $"id {Settings.InstanceId[..8]}…, IP {NetworkInfo.DescribeLocalAddresses()}, puerto {Settings.Port}, " +
                        $"{Settings.Contacts.Count} contacto(s)");

        _overlay = new OverlayController(Settings.Overlay, m => Link.ReportShown(m), RequestTrim);
        Link = CreateLink();
        if (Settings.ShowTrayIcon) CreateTray();
        _main = new MainWindow(this);
        try
        {
            _hotkey = new GlobalHotkey(OnHotkey);
            ApplyHotkey(Settings.SendHotkey);
        }
        catch (Exception ex)
        {
            Log.Error("hotkey", "No se pudo inicializar el atajo global", ex);
        }

        AutoStart.Repair(Settings.StartWithWindows, Args.Profile);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _instance.ListenForShowRequests(() => _ui.BeginInvoke(ShowMain));

        // Sin nombre no se sale a la red: nunca se muestra el nombre del equipo a los demás.
        if (IsReady) Link.Start();

        var startHidden = Args.AutoStart || Args.Minimized || Settings.StartMinimized;
        if (!IsReady)
        {
            ShowWelcome(); // la ventana principal se abre al cerrarla
        }
        else if (startHidden && HasTray)
        {
            RequestTrim(); // solo el icono de la bandeja
        }
        else if (startHidden)
        {
            _main.WindowState = WindowState.Minimized;
            _main.Show();
        }
        else
        {
            ShowMain();
        }
    }

    private PeerLink CreateLink()
    {
        var link = new PeerLink(new PeerLinkOptions
        {
            Identity = _identity,
            LocalName = Settings.FriendlyName,
            Port = Settings.Port,
            DiscoveryPort = Settings.DiscoveryPort,
        });

        link.StateChanged += s => _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(link, Link)) return;
            _tray?.SetState(s);
            LinkStateChanged?.Invoke(s);
        });
        link.ContactsChanged += () => _ui.BeginInvoke(() =>
        {
            if (ReferenceEquals(link, Link)) ContactsChanged?.Invoke();
        });
        link.ContactsSaved += contacts => _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(link, Link)) return;
            Settings.Contacts = contacts.Select(c => c.Clone()).ToList();
            Save();
        });
        link.MessageReceived += m => _ui.BeginInvoke(() => _overlay.Enqueue(m));
        link.DeliveryChanged += (id, st) => _ui.BeginInvoke(() => DeliveryChanged?.Invoke(id, st));
        link.SetContacts(Settings.Contacts);
        return link;
    }

    private async Task RestartLinkAsync()
    {
        var old = Link;
        Link = CreateLink();
        try { await old.StopAsync(); } catch (Exception ex) { Log.Warn("app", "Error deteniendo la conexión anterior", ex); }
        if (IsReady) Link.Start();
        var state = Link.State;
        _tray?.SetState(state);
        LinkStateChanged?.Invoke(state);
        ContactsChanged?.Invoke();
    }

    private void CreateTray()
    {
        if (_tray != null) return;
        try
        {
            _tray = new TrayIcon(ToggleMain, ShowMain, HideMain, ShowSettings, () => _ = ExitAsync(), () => _main?.IsVisible == true && _main.WindowState != WindowState.Minimized);
            _tray.SetState(Link.State);
        }
        catch (Exception ex)
        {
            Log.Error("app", "No se pudo crear el icono de la bandeja", ex);
            _tray = null;
        }
    }

    // ------------------------------------------------------------------ acciones

    /// <param name="recipient">InstanceId de la persona, o <see cref="AppSettings.AllRecipients"/> para todos los conectados.</param>
    public SendOutcome SendMessage(string recipient, string text, bool urgent)
    {
        if (!IsReady) return SendOutcome.Fail("Primero elegí tu nombre.");
        if (!MessageRules.TryValidate(text, out _, out var error)) return SendOutcome.Fail(error ?? "Mensaje inválido.");

        List<ContactInfo> targets;
        if (recipient == AppSettings.AllRecipients)
        {
            targets = Link.Contacts.Where(c => c.Status == ContactStatus.Online && !c.Blocked).ToList();
            if (targets.Count == 0) return SendOutcome.Fail("No hay nadie conectado ahora.");
        }
        else
        {
            var one = Link.FindContact(recipient);
            if (one == null) return SendOutcome.Fail("Elegí a quién enviarle el mensaje.");
            targets = new List<ContactInfo> { one };
        }

        var ids = new List<string>();
        string? firstError = null;
        foreach (var t in targets)
        {
            var r = Link.Send(t.Id, text, urgent, Settings.ConfirmDelivery);
            if (r.Accepted && r.MessageId != null) ids.Add(r.MessageId);
            else firstError ??= r.Error;
        }
        if (ids.Count == 0) return SendOutcome.Fail(firstError ?? "No se pudo enviar.");
        return new SendOutcome(true, ids, targets.Any(t => t.Status == ContactStatus.Online), null);
    }

    /// <summary>Recuerda a quién se le escribió por última vez (se preselecciona al abrir).</summary>
    public void RememberRecipient(string? recipient)
    {
        if (recipient == Settings.LastRecipient) return;
        Settings.LastRecipient = recipient;
        Save();
    }

    public void SetBlocked(string id, bool blocked) => Link.SetBlocked(id, blocked);

    public void ForgetContact(string id) => Link.Forget(id);

    public Task<AddContactResult> AddContactAsync(string address, CancellationToken ct) => Link.AddByAddressAsync(address, ct);

    public void ShowTestMessage(OverlaySettings preview, bool urgent) =>
        _overlay.ShowTest(SettingsValidator.NormalizeOverlay(preview.Clone()), urgent,
            Settings.FriendlyName.Length > 0 ? Settings.FriendlyName : "Susurro");

    public void Save() => _store.Save(Settings);

    /// <summary>Aplica la configuración editada en la ventana de configuración.</summary>
    public void ApplySettings(AppSettings edited)
    {
        var old = Settings;
        var updated = SettingsValidator.Normalize(edited.Clone(), Environment.MachineName);
        updated.InstanceId = old.InstanceId;
        updated.IdentityKey = old.IdentityKey;
        updated.SetupCompleted = old.SetupCompleted;
        updated.MainWindowLeft = old.MainWindowLeft;
        updated.MainWindowTop = old.MainWindowTop;
        updated.LastRecipient = old.LastRecipient;
        // Los contactos los gestiona la red (se agregan, bloquean y quitan al instante).
        updated.Contacts = old.Contacts.Select(c => c.Clone()).ToList();
        if (updated.FriendlyName.Length == 0) updated.FriendlyName = old.FriendlyName;
        Settings = updated;

        if (updated.FriendlyName != old.FriendlyName)
        {
            Link.UpdateLocalName(updated.FriendlyName);
            SetupChanged?.Invoke();
        }
        if (updated.StartWithWindows != old.StartWithWindows) AutoStart.Set(updated.StartWithWindows, Args.Profile);
        if (updated.SendHotkey != old.SendHotkey) ApplyHotkey(updated.SendHotkey);
        _overlay.UpdateSettings(updated.Overlay);

        if (updated.ShowTrayIcon && _tray == null) CreateTray();
        else if (!updated.ShowTrayIcon && _tray != null)
        {
            _tray.Dispose();
            _tray = null;
            if (!_main.IsVisible) ShowMain();
        }
        _main.ShowInTaskbar = true;

        Save();
        Log.Info("config", "Configuración guardada");

        if (updated.Port != old.Port || updated.DiscoveryPort != old.DiscoveryPort)
        {
            Log.Info("config", $"Puerto cambiado a {updated.Port}: reiniciando la comunicación");
            _ = RestartLinkAsync();
        }
    }

    /// <summary>Bienvenida completada: se guarda el nombre de la persona y se empieza a buscar compañeros.</summary>
    public bool CompleteSetup(string friendlyName)
    {
        var name = SettingsValidator.CleanName(friendlyName);
        if (name.Length == 0) return false;
        if (name != Settings.FriendlyName)
        {
            Settings.FriendlyName = name;
            Link.UpdateLocalName(name);
            Log.Info("config", "Nombre: " + name);
        }
        Settings.SetupCompleted = true;
        Save();
        Link.Start();
        SetupChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ ventanas

    public void ShowMain()
    {
        if (_exiting) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.FocusInput();
    }

    public void HideMain()
    {
        // Si se abrió con el atajo, se devuelve el foco a la ventana en la que el usuario estaba.
        var returnTo = _quickMode ? _quickReturnTo : IntPtr.Zero;
        _quickMode = false;
        _quickReturnTo = IntPtr.Zero;
        if (_tray == null)
        {
            _main.WindowState = WindowState.Minimized;
        }
        else
        {
            RememberMainPosition();
            _main.Hide();
            RequestTrim();
        }
        if (returnTo != IntPtr.Zero) Native.NativeMethods.SetForegroundWindow(returnTo);
    }

    // ------------------------------------------------------------------ atajo global

    private void ApplyHotkey(string? text)
    {
        if (_hotkey == null) return;
        HotkeyGesture.TryParse(text, out var gesture);
        if (!string.IsNullOrWhiteSpace(text) && gesture == null)
            Log.Warn("hotkey", $"Atajo inválido en la configuración: «{text}»");
        var ok = _hotkey.Set(gesture);
        if (gesture != null && ok) Log.Info("hotkey", "Atajo global: " + gesture.Display());
        HotkeyChanged?.Invoke();
    }

    /// <summary>¿Se puede usar esta combinación? (no la usa Windows ni otro programa)</summary>
    public bool CanUseHotkey(HotkeyGesture gesture) => _hotkey?.CanRegister(gesture) ?? false;

    /// <summary>Mientras se elige un atajo nuevo, el actual no debe interceptar las teclas.</summary>
    public void SuspendHotkey() => _hotkey?.Suspend();

    public void ResumeHotkey() => _hotkey?.Resume();

    private void OnHotkey()
    {
        if (_exiting) return;
        // Segunda pulsación con la ventana rápida abierta: cerrarla y volver.
        if (_quickMode && _main.IsVisible && _main.IsActive)
        {
            HideMain();
            return;
        }
        var fg = Native.NativeMethods.GetForegroundWindow();
        var ownHandle = new System.Windows.Interop.WindowInteropHelper(_main).Handle;
        _quickReturnTo = fg != ownHandle ? fg : IntPtr.Zero;
        _quickMode = _quickReturnTo != IntPtr.Zero;
        ShowMain();
    }

    /// <summary>Tras enviar: si se abrió con el atajo y hay conexión, se oculta y se vuelve al trabajo.</summary>
    public void AfterSend(bool anyOnline)
    {
        if (!_quickMode) return;
        if (!anyOnline) return; // sin conexión: dejar visible el estado "En espera"
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_quickMode && _main.IsActive) HideMain();
        };
        timer.Start();
    }

    /// <summary>El usuario pasó a otra ventana por su cuenta: ya no hay a dónde "volver".</summary>
    public void OnMainDeactivated()
    {
        _quickMode = false;
        _quickReturnTo = IntPtr.Zero;
    }

    public void ToggleMain()
    {
        if (_main.IsVisible && _main.WindowState != WindowState.Minimized && _main.IsActive) HideMain();
        else ShowMain();
    }

    public void ShowSettings() => ShowSettings(null);

    /// <param name="tab">Pestaña a mostrar (índice), o null para dejar la actual.</param>
    public void ShowSettings(int? tab)
    {
        if (_exiting) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                RequestTrim();
            };
            _settingsWindow.Show();
        }
        if (tab is int t) _settingsWindow.SelectTab(t);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ShowWelcome()
    {
        if (_exiting) return;
        if (_welcomeWindow == null)
        {
            _welcomeWindow = new WelcomeWindow(this);
            _welcomeWindow.Closed += (_, _) =>
            {
                _welcomeWindow = null;
                RequestTrim();
                ShowMain();
            };
            _welcomeWindow.Show();
        }
        _welcomeWindow.Activate();
    }

    public void ShowLog()
    {
        if (_exiting) return;
        if (_logWindow == null)
        {
            _logWindow = new LogWindow(this);
            _logWindow.Closed += (_, _) =>
            {
                _logWindow = null;
                RequestTrim();
            };
            _logWindow.Show();
        }
        _logWindow.Activate();
    }

    public void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("app", "No se pudo abrir la carpeta", ex); }
    }

    private void RememberMainPosition()
    {
        if (_main.WindowState != WindowState.Normal || !_main.IsVisible) return;
        if (Settings.MainWindowLeft == _main.Left && Settings.MainWindowTop == _main.Top) return;
        Settings.MainWindowLeft = _main.Left;
        Settings.MainWindowTop = _main.Top;
        Save();
    }

    private bool AnyWindowVisible() =>
        Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible && w.WindowState != WindowState.Minimized);

    /// <summary>Tras cerrar ventanas u overlays, devuelve memoria al sistema (una vez, no periódico).</summary>
    public void RequestTrim()
    {
        if (_exiting) return;
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    // ------------------------------------------------------------------ sistema

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("app", "Reanudación tras suspensión");
            Link.NotifyNetworkChanged();
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Link.NotifyNetworkChanged();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Link.NotifyNetworkChanged();

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("app", "Cerrando Susurro");
        RememberMainPosition();
        _overlay.Clear();
        foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
        {
            try { w.Close(); } catch { }
        }
        try { await Link.StopAsync().WaitAsync(TimeSpan.FromSeconds(1.5)); }
        catch { }
        Application.Current.Shutdown();
    }

    public void OnSessionEnding()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("app", "Windows está cerrando la sesión");
        try { Link.StopAsync().Wait(TimeSpan.FromMilliseconds(800)); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _trimTimer.Stop();
        _tray?.Dispose();
        _tray = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _instance.Dispose();
        _identity?.Dispose();
        Log.Info("app", "Fin");
    }
}
