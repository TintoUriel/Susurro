using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using Susurro.Core.Transfers;
using Susurro.Core.Updates;

namespace Susurro.App;

/// <summary>Qué se envió con cada id (para mostrar el estado adecuado).</summary>
internal enum SentKind { Text, Image, File }

/// <summary>Resultado de enviar a una persona o a todos los conectados.</summary>
/// <param name="Warning">Algo no se pudo enviar a alguien (p. ej. versión anterior), aunque el resto sí.</param>
internal sealed record SendOutcome(bool Accepted, IReadOnlyDictionary<string, SentKind> Sent, bool AnyOnline, string? Error, string? Warning = null)
{
    public IReadOnlyCollection<string> MessageIds => (IReadOnlyCollection<string>)Sent.Keys;
    public static SendOutcome Fail(string error) => new(false, new Dictionary<string, SentKind>(), false, error);
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
    private FilesPanel _files = null!;
    private readonly Dictionary<string, (string Name, DateTime Until)> _typing = new();
    private readonly DispatcherTimer _typingTimer;
    private GlobalHotkey? _hotkey;
    private IntPtr _quickReturnTo;
    private bool _quickMode;
    private MainWindow _main = null!;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private LogWindow? _logWindow;
    private ShortcutsWindow? _shortcutsWindow;
    private Updater? _updater;
    private readonly DispatcherTimer _updateRestartTimer;
    private System.Version? _stagedVersion;
    private bool _restartingForUpdate;
    private bool _exiting;
    private bool _disposed;

    public AppController(CommandLine args, SingleInstance instance, Dispatcher ui)
    {
        Args = args;
        _instance = instance;
        _ui = ui;
        _trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _typingTimer = new DispatcherTimer(DispatcherPriority.Background);
        _typingTimer.Tick += (_, _) => ExpireTyping();
        _updateRestartTimer = new DispatcherTimer(DispatcherPriority.Background);
        _updateRestartTimer.Tick += (_, _) => OnUpdateRestartTimer();
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
    /// <summary>Cambió quién te está escribiendo (ver <see cref="TypingText"/>).</summary>
    public event Action? TypingChanged;
    /// <summary>Progreso de algo que enviaste: id, bytes, total.</summary>
    public event Action<string, long, long>? SendProgress;

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

        _overlay = new OverlayController(Settings.Overlay, m => Link.ReportShown(m), RequestTrim, SaveImage);
        _files = new FilesPanel(() => Settings.Overlay);
        _files.DownloadRequested += DownloadFile;
        _files.CloseRequested += id =>
        {
            Link.DeclineFile(id);
            _files.Remove(id);
            RequestTrim();
        };
        _files.OpenRequested += path => Launch(path, null);
        _files.ShowInFolderRequested += path => Launch("explorer.exe", $"/select,\"{path}\"");
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
        StartUpdater();
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
            TransferTempDirectory = Path.Combine(DataDirectory, "incoming"),
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
        link.MessageReceived += m => _ui.BeginInvoke(() =>
        {
            if (m.SenderId != null) SetTyping(m.SenderId, null, false); // ya llegó lo que escribía
            _overlay.Enqueue(m);
        });
        link.FileOffered += f => _ui.BeginInvoke(() =>
        {
            SetTyping(f.SenderId, null, false);
            _files.Add(f);
        });
        link.TransferProgress += (id, done, total) => _ui.BeginInvoke(() =>
        {
            _files.SetProgress(id, done, total);
            SendProgress?.Invoke(id, done, total);
        });
        link.TransferCompleted += (id, path) => _ui.BeginInvoke(() => _files.SetCompleted(id, path));
        link.TransferFailed += (id, reason) => _ui.BeginInvoke(() => _files.SetFailed(id, reason));
        link.TypingChanged += (id, name, on) => _ui.BeginInvoke(() => SetTyping(id, name, on));
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
            _tray = new TrayIcon(ToggleMain, ShowMain, HideMain, ShowSettings, ShowShortcuts, () => _ = ExitAsync(), () => _main?.IsVisible == true && _main.WindowState != WindowState.Minimized);
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
    /// <param name="attachments">Imagen pegada y/o archivos. Con una imagen, el texto va como pie de foto.</param>
    public SendOutcome SendMessage(string recipient, string text, bool urgent, IReadOnlyList<Attachment>? attachments = null)
    {
        if (!IsReady) return SendOutcome.Fail("Primero elegí tu nombre.");
        attachments ??= Array.Empty<Attachment>();
        var hasText = MessageRules.Sanitize(text).Length > 0;
        if (attachments.Count == 0 || hasText)
        {
            if (!MessageRules.TryValidate(text, out _, out var error)) return SendOutcome.Fail(error ?? "Mensaje inválido.");
        }

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

        var sent = new Dictionary<string, SentKind>();
        string? firstError = null;
        var images = attachments.OfType<ImageAttachment>().ToList();
        var files = attachments.OfType<FileAttachment>().ToList();
        foreach (var t in targets)
        {
            void Track(SendResult r, SentKind kind)
            {
                if (r.Accepted && r.MessageId != null) sent[r.MessageId] = kind;
                else firstError ??= r.Error;
            }
            for (var i = 0; i < images.Count; i++)
                Track(Link.SendImage(t.Id, images[i].Data, i == 0 ? text : null, urgent, Settings.ConfirmDelivery), SentKind.Image);
            foreach (var f in files)
                Track(Link.OfferFile(t.Id, f.Path, Settings.ConfirmDelivery), SentKind.File);
            if (hasText && images.Count == 0)
                Track(Link.Send(t.Id, text, urgent, Settings.ConfirmDelivery), SentKind.Text);
        }
        if (sent.Count == 0) return SendOutcome.Fail(firstError ?? "No se pudo enviar.");
        if (attachments.Count > 0) SetTypingOffAfterSend(targets);
        return new SendOutcome(true, sent, targets.Any(t => t.Status == ContactStatus.Online), null, firstError);
    }

    private void SetTypingOffAfterSend(IEnumerable<ContactInfo> targets) => Link.SendTyping(targets.Select(t => t.Id), false);

    // ------------------------------------------------------------------ "está escribiendo"

    /// <summary>La ventana avisa que estás (o ya no) escribiéndole a alguien o a todos los conectados.</summary>
    public void NotifyTyping(string? recipient, bool typing)
    {
        if (!IsReady || recipient == null) return;
        var ids = recipient == AppSettings.AllRecipients
            ? Link.Contacts.Where(c => c.Status == ContactStatus.Online && !c.Blocked).Select(c => c.Id)
            : new[] { recipient };
        Link.SendTyping(ids, typing);
    }

    /// <summary>"Ana está escribiendo…", "Ana y Beto están escribiendo…" o null.</summary>
    public string? TypingText
    {
        get
        {
            var names = _typing.Values.Select(v => v.Name).Distinct().ToList();
            return names.Count switch
            {
                0 => null,
                1 => $"{names[0]} está escribiendo…",
                2 => $"{names[0]} y {names[1]} están escribiendo…",
                _ => $"{names.Count} personas están escribiendo…",
            };
        }
    }

    /// <summary>
    /// Quien escribe avisa al teclear (cada pocos segundos); si deja de llegar el aviso, se borra solo
    /// a los 6 s. Un único temporizador de un disparo, programado al vencimiento más cercano.
    /// </summary>
    private void SetTyping(string peerId, string? name, bool on)
    {
        var changed = on
            ? !_typing.ContainsKey(peerId)
            : _typing.Remove(peerId);
        if (on) _typing[peerId] = (SettingsValidator.CleanName(name) is { Length: > 0 } n ? n : "Alguien", DateTime.UtcNow.AddSeconds(6));
        ScheduleTypingExpiry();
        if (changed) TypingChanged?.Invoke();
    }

    private void ExpireTyping()
    {
        _typingTimer.Stop();
        var now = DateTime.UtcNow;
        var expired = _typing.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList();
        foreach (var id in expired) _typing.Remove(id);
        ScheduleTypingExpiry();
        if (expired.Count > 0) TypingChanged?.Invoke();
    }

    private void ScheduleTypingExpiry()
    {
        _typingTimer.Stop();
        if (_typing.Count == 0) return;
        var next = _typing.Values.Min(v => v.Until) - DateTime.UtcNow;
        _typingTimer.Interval = next < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : next;
        _typingTimer.Start();
    }

    // ------------------------------------------------------------------ imágenes y archivos recibidos

    private void DownloadFile(string id)
    {
        var error = Link.DownloadFile(id, Attachments.DownloadsFolder());
        if (error != null) _files.SetFailed(id, error);
        else _files.SetDownloading(id);
    }

    /// <summary>Guarda una imagen recibida en Descargas ("Imagen de Ana 2026-09-25 12.30.05.png").</summary>
    private string? SaveImage(WhisperMessage message)
    {
        if (message.Image == null) return null;
        try
        {
            var dir = Attachments.DownloadsFolder();
            Directory.CreateDirectory(dir);
            var name = FileNames.Sanitize($"Imagen de {message.SenderName} {message.SentAt.ToLocalTime():yyyy-MM-dd HH.mm.ss}{Attachments.ImageExtension(message.Image)}", "imagen.png");
            var path = FileNames.UniquePath(dir, name);
            File.WriteAllBytes(path, message.Image);
            Log.Info("files", "Imagen guardada en Descargas");
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("files", "No se pudo guardar la imagen", ex);
            return null;
        }
    }

    private static void Launch(string file, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("files", "No se pudo abrir", ex);
        }
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
        if (updated.AutoUpdate != old.AutoUpdate)
        {
            if (updated.AutoUpdate)
            {
                _updater?.Start();
                if (_stagedVersion != null) ScheduleUpdateRestart(TimeSpan.FromMinutes(1));
            }
            else
            {
                _updater?.Stop();
                _updateRestartTimer.Stop(); // si ya se descargó, se usa recién en el próximo arranque
            }
        }
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

    public void ShowShortcuts()
    {
        if (_exiting) return;
        if (_shortcutsWindow == null)
        {
            _shortcutsWindow = new ShortcutsWindow(this);
            _shortcutsWindow.Closed += (_, _) =>
            {
                _shortcutsWindow = null;
                RequestTrim();
            };
            _shortcutsWindow.Show();
        }
        if (_shortcutsWindow.WindowState == WindowState.Minimized) _shortcutsWindow.WindowState = WindowState.Normal;
        _shortcutsWindow.Activate();
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
            _updater?.NotifyResumed();
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Link.NotifyNetworkChanged();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Link.NotifyNetworkChanged();

    // ------------------------------------------------------------------ actualización automática

    /// <summary>Si el reinicio no se puede hacer ahora, se vuelve a mirar en este tiempo (solo con una versión lista).</summary>
    private static readonly TimeSpan UpdateRestartRetry = TimeSpan.FromMinutes(5);
    /// <summary>Sin tocar teclado ni mouse al menos este tiempo, para que el reinicio no se note.</summary>
    private static readonly TimeSpan UpdateRequiredIdle = TimeSpan.FromMinutes(2);
    /// <summary>La versión nueva tiene este tiempo para avisar que arrancó (el antivirus puede revisarla antes).</summary>
    private static readonly TimeSpan UpdateStartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>null si esta instalación se actualiza sola; si no, el motivo (para la configuración).</summary>
    public string? UpdateUnavailableReason { get; private set; }

    /// <summary>Estado de la actualización automática para la configuración.</summary>
    public string UpdateStatusText
    {
        get
        {
            if (UpdateUnavailableReason != null) return UpdateUnavailableReason;
            if (_stagedVersion != null)
                return $"La versión {_stagedVersion.ToString(3)} ya está descargada: se aplica sola cuando no estés usando la PC.";
            var result = _updater?.LastResult;
            var when = _updater?.LastCheckUtc;
            if (result == null || when == null)
                return Settings.AutoUpdate ? "Se busca un rato después de arrancar y después una vez por día." : "Desactivada.";
            var at = when.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            return result.Outcome switch
            {
                UpdateOutcome.UpToDate => $"Al día (se buscó a las {at}).",
                UpdateOutcome.NotWritable => "No se puede actualizar sola: la carpeta del programa no admite escritura. Instalá una vez SusurroSetup.exe 2.2.0 o posterior.",
                _ => $"No se pudo buscar a las {at} (¿sin internet?). Se reintenta en 24 horas.",
            };
        }
    }

    private void StartUpdater()
    {
        var exe = AppPaths.ExecutablePath;
#if DEBUG
        UpdateUnavailableReason = "Compilación de desarrollo: no se actualiza sola.";
#else
        if (Args.Profile != null)
            UpdateUnavailableReason = "Perfil de desarrollo: no se actualiza solo.";
        else if (!string.Equals(Path.GetFileName(exe), "Susurro.exe", StringComparison.OrdinalIgnoreCase))
            UpdateUnavailableReason = "Solo se actualiza sola la versión publicada (Susurro.exe).";
#endif
        if (UpdateUnavailableReason != null) return;

        Updater.CleanupLeftovers(exe); // lo que quedó de la versión anterior
        if (Args.UpdatedFrom != null) Log.Info("update", $"Actualizado a {Version}");
        _updater = new Updater(new UpdateOptions
        {
            CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new System.Version(1, 0, 0),
            ExecutablePath = exe,
        });
        _updater.Staged += v => _ui.BeginInvoke(() => OnUpdateStaged(v));
        if (Settings.AutoUpdate) _updater.Start();
    }

    private void OnUpdateStaged(System.Version version)
    {
        if (_exiting) return;
        _stagedVersion = version;
        if (Settings.AutoUpdate) ScheduleUpdateRestart(TimeSpan.FromMinutes(1));
    }

    private void ScheduleUpdateRestart(TimeSpan delay)
    {
        _updateRestartTimer.Stop();
        _updateRestartTimer.Interval = delay;
        _updateRestartTimer.Start();
    }

    private void OnUpdateRestartTimer()
    {
        _updateRestartTimer.Stop();
        if (_exiting || _restartingForUpdate || _stagedVersion is not System.Version version || !Settings.AutoUpdate) return;
        if (!CanRestartUnnoticed())
        {
            ScheduleUpdateRestart(UpdateRestartRetry);
            return;
        }
        _ = RestartForUpdateAsync(version);
    }

    /// <summary>Nada abierto ni en pantalla, nada pendiente de enviar o recibir y la persona sin usar la PC.</summary>
    private bool CanRestartUnnoticed() =>
        !AnyWindowVisible() && _overlay.IsIdle && _files.IsEmpty && _typing.Count == 0 && !Link.IsBusy &&
        Native.NativeMethods.IdleTime() >= UpdateRequiredIdle;

    /// <summary>
    /// Lanza la versión nueva (oculta en la bandeja) y termina. Si la nueva no avisa que arrancó,
    /// se la detiene, se vuelve al ejecutable anterior y esta sigue funcionando como si nada.
    /// </summary>
    private async Task RestartForUpdateAsync(System.Version version)
    {
        _restartingForUpdate = true;
        var exe = AppPaths.ExecutablePath;
        Log.Info("update", $"Reiniciando para usar la versión {version.ToString(3)}");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, SingleInstance.UpdateStartedEventName(Environment.ProcessId));
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            psi.ArgumentList.Add("--updated");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--minimized");
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("update", "No se pudo iniciar la versión nueva", ex);
        }

        var ok = process != null && await Task.Run(() => started.WaitOne(UpdateStartTimeout));
        if (ok)
        {
            process!.Dispose();
            await ExitAsync();
            return;
        }

        try
        {
            if (process is { HasExited: false }) process.Kill();
        }
        catch
        {
        }
        process?.Dispose();
        Updater.Rollback(exe);
        _updater?.Reject(version);
        _stagedVersion = null;
        _restartingForUpdate = false;
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("app", "Cerrando Susurro");
        RememberMainPosition();
        _overlay.Clear();
        _files.Dispose();
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
        _typingTimer.Stop();
        _updateRestartTimer.Stop();
        _updater?.Dispose();
        _updater = null;
        _tray?.Dispose();
        _tray = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _instance.Dispose();
        _identity?.Dispose();
        Log.Info("app", "Fin");
    }
}
