using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Susurro.App.Overlay;
using Susurro.App.Services;
using Susurro.App.Tray;
using Susurro.App.Views;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Messaging;
using Susurro.Core.Net;
using Susurro.Core.Pairing;

namespace Susurro.App;

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
    private OverlayController _overlay = null!;
    private TrayIcon? _tray;
    private MainWindow _main = null!;
    private SettingsWindow? _settingsWindow;
    private PairingWindow? _pairingWindow;
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

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public event Action<LinkState>? LinkStateChanged;
    public event Action<string, DeliveryState>? DeliveryChanged;
    public event Action? PeerChanged;
    public event Action? InvitationClosed;

    // ------------------------------------------------------------------ arranque

    public void Start()
    {
        LogSink = new FileLogSink(AppPaths.LogDirectory(Args.Profile));
        Log.Initialize(LogSink);

        _store = new SettingsStore(DataDirectory);
        Settings = _store.Load(Environment.MachineName, out var existed);
        var changed = !existed;
        // Primera ejecución: inicio con Windows activado (se desactiva en Configuración → General).
        // Los perfiles de desarrollo (--profile) no se agregan al inicio de Windows.
        if (!existed && Args.Profile != null) Settings.StartWithWindows = false;
        if (Args.Port is int port && port != Settings.Port) { Settings.Port = port; changed = true; }
        if (Args.DiscoveryPort is int dport && dport != Settings.DiscoveryPort) { Settings.DiscoveryPort = dport; changed = true; }
        Settings = SettingsValidator.Normalize(Settings, Environment.MachineName);
        if (changed) Save();

        Log.Info("app", $"Inicio de Susurro {Version}{(Args.Profile != null ? $" (perfil {Args.Profile})" : "")} — «{Settings.FriendlyName}», " +
                        $"id {Settings.InstanceId[..8]}…, IP {NetworkInfo.DescribeLocalAddresses()}, puerto {Settings.Port}");

        _overlay = new OverlayController(Settings.Overlay, m => Link.ReportShown(m), RequestTrim);
        Link = CreateLink();
        if (Settings.ShowTrayIcon) CreateTray();
        _main = new MainWindow(this);

        AutoStart.Repair(Settings.StartWithWindows, Args.Profile);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _instance.ListenForShowRequests(() => _ui.BeginInvoke(ShowMain));

        Link.Start();

        var startHidden = Args.AutoStart || Args.Minimized || Settings.StartMinimized;
        if (!Settings.SetupCompleted && !Args.AutoStart)
        {
            ShowMain();
            ShowPairing(firstRun: true);
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
            InstanceId = Settings.InstanceId,
            LocalName = Settings.FriendlyName,
            Port = Settings.Port,
            DiscoveryPort = Settings.DiscoveryPort,
        }, new DpapiKeyProtector());

        link.StateChanged += s => _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(link, Link)) return;
            _tray?.SetState(s);
            LinkStateChanged?.Invoke(s);
        });
        link.MessageReceived += m => _ui.BeginInvoke(() => _overlay.Enqueue(m));
        link.DeliveryChanged += (id, st) => _ui.BeginInvoke(() => DeliveryChanged?.Invoke(id, st));
        link.PeerChanged += p => _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(link, Link)) return;
            Settings.Peer = p?.Clone();
            if (p != null) Settings.SetupCompleted = true;
            Save();
            PeerChanged?.Invoke();
        });
        link.InvitationClosed += () => _ui.BeginInvoke(() => InvitationClosed?.Invoke());
        link.SetPeer(Settings.Peer);
        return link;
    }

    private async Task RestartLinkAsync()
    {
        var old = Link;
        Link = CreateLink();
        try { await old.StopAsync(); } catch (Exception ex) { Log.Warn("app", "Error deteniendo la conexión anterior", ex); }
        Link.Start();
        var state = Link.State;
        _tray?.SetState(state);
        LinkStateChanged?.Invoke(state);
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

    public SendResult SendMessage(string text, bool urgent) => Link.Send(text, urgent, Settings.ConfirmDelivery);

    public void ShowTestMessage(OverlaySettings preview, bool urgent) =>
        _overlay.ShowTest(SettingsValidator.NormalizeOverlay(preview.Clone()), urgent, Settings.FriendlyName);

    public void Save() => _store.Save(Settings);

    /// <summary>Aplica la configuración editada en la ventana de configuración.</summary>
    public void ApplySettings(AppSettings edited)
    {
        var old = Settings;
        var updated = SettingsValidator.Normalize(edited.Clone(), Environment.MachineName);
        updated.InstanceId = old.InstanceId;
        updated.SetupCompleted = old.SetupCompleted;
        updated.MainWindowLeft = old.MainWindowLeft;
        updated.MainWindowTop = old.MainWindowTop;
        // El vínculo lo gestiona la red; de la ventana solo se toma la dirección manual.
        var manual = edited.Peer?.ManualAddress;
        updated.Peer = old.Peer?.Clone();
        if (updated.Peer != null) updated.Peer.ManualAddress = string.IsNullOrWhiteSpace(manual) ? null : manual.Trim();
        Settings = updated;

        if (updated.FriendlyName != old.FriendlyName) Link.UpdateLocalName(updated.FriendlyName);
        if (updated.StartWithWindows != old.StartWithWindows) AutoStart.Set(updated.StartWithWindows, Args.Profile);
        _overlay.UpdateSettings(updated.Overlay);
        if (updated.Peer != null && updated.Peer.ManualAddress != old.Peer?.ManualAddress)
            Link.UpdateManualAddress(updated.Peer.ManualAddress);

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

    public void SetFriendlyName(string friendlyName)
    {
        var name = SettingsValidator.CleanName(friendlyName);
        if (name.Length == 0 || name == Settings.FriendlyName) return;
        Settings.FriendlyName = name;
        Link.UpdateLocalName(name);
        Save();
        Log.Info("config", "Nombre de esta PC: " + name);
    }

    public void CompleteSetup(string friendlyName)
    {
        SetFriendlyName(friendlyName);
        if (Settings.SetupCompleted) return;
        Settings.SetupCompleted = true;
        Save();
    }

    public void Unpair()
    {
        Link.Unpair();
        Settings.Peer = null;
        Save();
        PeerChanged?.Invoke();
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
        if (_tray == null)
        {
            _main.WindowState = WindowState.Minimized;
            return;
        }
        RememberMainPosition();
        _main.Hide();
        RequestTrim();
    }

    public void ToggleMain()
    {
        if (_main.IsVisible && _main.WindowState != WindowState.Minimized && _main.IsActive) HideMain();
        else ShowMain();
    }

    public void ShowSettings()
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
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ShowPairing(bool firstRun = false)
    {
        if (_exiting) return;
        if (_pairingWindow == null)
        {
            _pairingWindow = new PairingWindow(this, firstRun);
            _pairingWindow.Closed += (_, _) =>
            {
                _pairingWindow = null;
                RequestTrim();
            };
            _pairingWindow.Show();
        }
        _pairingWindow.Activate();
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
        _instance.Dispose();
        Log.Info("app", "Fin");
    }
}
