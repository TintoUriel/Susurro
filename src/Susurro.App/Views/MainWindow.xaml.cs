using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Susurro.App.Services;
using Susurro.Core.Config;
using Susurro.Core.Messaging;
using Susurro.Core.Net;

namespace Susurro.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Opción del selector "Para": una persona o todos los conectados.</summary>
    public sealed record RecipientOption(string Id, string Label, string Dot, Brush DotBrush, string? Note);

    private readonly AppController _app;
    private readonly DispatcherTimer _feedbackTimer;
    /// <summary>Estado de entrega de cada mensaje del último envío (uno por destinatario).</summary>
    private readonly Dictionary<string, DeliveryState?> _lastSend = new();
    private readonly Dictionary<string, SentKind> _lastKinds = new();
    private readonly List<Attachment> _attachments = new();
    private const int MaxAttachments = 10;
    // "está escribiendo": se avisa al teclear, como mucho cada 3 s; nunca por temporizador.
    private bool _typingOn;
    private string? _typingTarget;
    private DateTime _typingSentAt;
    private bool _positioned;
    private bool _updatingRecipients;
    private bool _recipientsDirty;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            Feedback.Text = "";
        };

        _app.LinkStateChanged += ApplyState;
        _app.DeliveryChanged += OnDeliveryChanged;
        _app.ContactsChanged += RefreshRecipients;
        _app.SetupChanged += () =>
        {
            ApplyState(_app.Link.State);
            RefreshRecipients();
        };
        ApplyState(_app.Link.State);
        RefreshRecipients();

        Loaded += (_, _) => PlaceWindow();
        Activated += (_, _) => FocusInput();
        Deactivated += (_, _) => _app.OnMainDeactivated();
        _app.HotkeyChanged += UpdateHotkeyHint;
        UpdateHotkeyHint();
        _app.TypingChanged += UpdateTyping;
        _app.SendProgress += OnSendProgress;
        CommandManager.AddPreviewExecutedHandler(Input, OnPreviewExecuted);
        CommandManager.AddPreviewCanExecuteHandler(Input, OnPreviewCanExecute);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopTyping();
        };
        Closing += OnClosing;
        UpdateCloseTooltip();
    }

    public void FocusInput()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
        });
    }

    private void UpdateHotkeyHint() =>
        Input.ToolTip = _app.HotkeyActive && _app.Hotkey != null
            ? $"Desde cualquier programa: {_app.Hotkey.Display()}"
            : null;

    private void UpdateCloseTooltip() =>
        CloseButton.ToolTip = _app.HasTray ? "Cerrar (Susurro sigue en la bandeja)" : "Salir de Susurro";

    // ------------------------------------------------------------------ posición

    private void PlaceWindow()
    {
        if (_positioned) return;
        _positioned = true;
        var work = SystemParameters.WorkArea;
        var left = _app.Settings.MainWindowLeft;
        var top = _app.Settings.MainWindowTop;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (left is double l && top is double t &&
            l >= virtualLeft - 20 && t >= virtualTop - 20 && l + 100 <= virtualRight && t + 60 <= virtualBottom)
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Por defecto: esquina inferior derecha, junto a la bandeja.
            Left = work.Right - ActualWidth - 16;
            Top = work.Bottom - ActualHeight - 16;
        }
    }

    // ------------------------------------------------------------------ estado

    private void ApplyState(LinkState state)
    {
        UpdateCloseTooltip();
        if (!_app.IsReady)
            SetStatus("○", "FaintBrush", "Sin nombre todavía");
        else if (state.Status == LinkStatus.Online)
            SetStatus("●", "OkBrush", state.Online == 1 ? "1 persona conectada" : $"{state.Online} personas conectadas");
        else if (state.Status == LinkStatus.NoneOnline)
            SetStatus("○", "FaintBrush", "Nadie conectado ahora");
        else
            SetStatus("○", "WarnBrush", "Buscando compañeros en la red…");

        StatusText.ToolTip = _app.IsReady ? $"Te ven como «{_app.Settings.FriendlyName}»" : null;
        StatusDetail.Text = state.Detail ?? "";
        StatusDetail.Visibility = string.IsNullOrEmpty(state.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ destinatarios

    private void RefreshRecipients()
    {
        // Cambiar la lista con el desplegable abierto lo cerraría: se actualiza al cerrarse.
        if (RecipientCombo.IsDropDownOpen)
        {
            _recipientsDirty = true;
            return;
        }
        _recipientsDirty = false;

        var contacts = _app.IsReady ? _app.Link.Contacts.Where(c => !c.Blocked).ToList() : new List<ContactInfo>();
        if (!_app.IsReady)
        {
            ShowRecipientMessage("Elegí tu nombre para empezar.", "Elegir nombre…");
        }
        else if (contacts.Count == 0)
        {
            ShowRecipientMessage("Todavía no apareció nadie en la red.", "¿No aparece nadie?");
        }
        else
        {
            RecipientCombo.Visibility = Visibility.Visible;
            PeerLabel.Visibility = Visibility.Collapsed;
            SetupButton.Visibility = Visibility.Collapsed;
        }
        SendButton.IsEnabled = contacts.Count > 0;

        var options = new List<RecipientOption>();
        if (contacts.Count > 1)
        {
            var online = contacts.Count(c => c.Status == ContactStatus.Online);
            options.Add(new RecipientOption(AppSettings.AllRecipients, "Todos los conectados", "✱", Brush("AccentBrush"), $"({online})"));
        }
        foreach (var c in contacts)
        {
            options.Add(c.Status switch
            {
                ContactStatus.Online => new RecipientOption(c.Id, c.Name, "●", Brush("OkBrush"), null),
                ContactStatus.Connecting => new RecipientOption(c.Id, c.Name, "○", Brush("WarnBrush"), "conectando…"),
                _ => new RecipientOption(c.Id, c.Name, "○", Brush("FaintBrush"), "desconectado"),
            });
        }

        var wanted = (RecipientCombo.SelectedItem as RecipientOption)?.Id ?? _app.Settings.LastRecipient;
        _updatingRecipients = true;
        try
        {
            RecipientCombo.ItemsSource = options;
            RecipientCombo.SelectedItem =
                options.FirstOrDefault(o => o.Id == wanted)
                ?? (contacts.Count == 1 ? options.FirstOrDefault() : options.FirstOrDefault(o => o.Id == AppSettings.AllRecipients));
        }
        finally
        {
            _updatingRecipients = false;
        }
    }

    private void ShowRecipientMessage(string text, string action)
    {
        RecipientCombo.Visibility = Visibility.Collapsed;
        PeerLabel.Visibility = Visibility.Visible;
        PeerLabel.Text = text;
        SetupButton.Content = action;
        SetupButton.Visibility = Visibility.Visible;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void RecipientCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingRecipients || RecipientCombo.SelectedItem is not RecipientOption o) return;
        _app.RememberRecipient(o.Id);
        // Si estaba escribiendo, el aviso pasa a la nueva persona.
        if (_typingOn && _typingTarget != o.Id)
        {
            StopTyping();
            UpdateTypingSignal();
        }
    }

    // ------------------------------------------------------------------ "está escribiendo"

    private void UpdateTyping()
    {
        var text = _app.TypingText;
        TypingText.Text = text != null ? "· " + text : "";
        TypingText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTypingSignal()
    {
        var target = (RecipientCombo.SelectedItem as RecipientOption)?.Id;
        if (Input.Text.Length == 0 || target == null)
        {
            StopTyping();
            return;
        }
        var now = DateTime.UtcNow;
        if (_typingOn && _typingTarget == target && now - _typingSentAt < TimeSpan.FromSeconds(3)) return;
        _typingOn = true;
        _typingTarget = target;
        _typingSentAt = now;
        _app.NotifyTyping(target, true);
    }

    private void StopTyping()
    {
        if (!_typingOn) return;
        _typingOn = false;
        _app.NotifyTyping(_typingTarget, false);
        _typingTarget = null;
    }

    // ------------------------------------------------------------------ adjuntos

    /// <summary>
    /// El TextBox deshabilita "Pegar" si el portapapeles no tiene texto: sin esto, Ctrl+V con una
    /// imagen o archivos copiados no haría nada.
    /// </summary>
    private void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || Clipboard.ContainsData("PNG"))
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // portapapeles ocupado: se deja el comportamiento normal
        }
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        var list = Attachments.FromClipboard(out var error);
        if (list is { Count: > 0 })
        {
            e.Handled = true;
            AddAttachments(list);
        }
        else if (error != null)
        {
            e.Handled = true;
            ShowFeedback(error, "ErrBrush", sticky: false);
        }
        // Si es texto, se pega normalmente.
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Enviar archivos", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) AddAttachments(Attachments.FromPaths(dialog.FileNames, out var error), error);
        FocusInput();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddAttachments(Attachments.FromPaths(paths, out var error), error);
            Activate();
            FocusInput();
        }
        e.Handled = true;
    }

    private void AddAttachments(IEnumerable<Attachment> items, string? error = null)
    {
        foreach (var a in items)
        {
            if (_attachments.Count >= MaxAttachments)
            {
                error = $"Máximo {MaxAttachments} adjuntos por envío.";
                break;
            }
            if (a is FileAttachment f && _attachments.OfType<FileAttachment>().Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase))) continue;
            _attachments.Add(a);
        }
        if (error != null) ShowFeedback(error, "ErrBrush", sticky: false);
        RenderAttachments();
    }

    private void RenderAttachments()
    {
        AttachmentsPanel.Children.Clear();
        foreach (var a in _attachments)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (a is ImageAttachment img)
                content.Children.Add(new Image { Source = img.Thumbnail, Height = 30, MaxWidth = 60, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) });
            else
                content.Children.Add(new TextBlock { Text = "\uE8A5", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Foreground = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = a.Label, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, ToolTip = a is FileAttachment f ? f.Path : a.Label });
            var remove = new Button { Content = "\uE711", Style = (Style)FindResource("ChromeButton"), Width = 24, Height = 24, FontSize = 9, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Quitar" };
            var item = a;
            remove.Click += (_, _) =>
            {
                _attachments.Remove(item);
                RenderAttachments();
                FocusInput();
            };
            content.Children.Add(remove);
            AttachmentsPanel.Children.Add(new Border
            {
                Child = content,
                Background = Brush("InputBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 2, 4),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }
        AttachmentsPanel.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Input.Tag = _attachments.OfType<ImageAttachment>().Any() ? "Agregar un comentario (opcional)…" : "Escribir mensaje…";
    }

    private void RecipientCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (_recipientsDirty) RefreshRecipients();
        FocusInput();
    }

    private void MoveRecipient(int delta)
    {
        var count = RecipientCombo.Items.Count;
        if (count < 2 || RecipientCombo.Visibility != Visibility.Visible) return;
        var index = RecipientCombo.SelectedIndex < 0 ? 0 : RecipientCombo.SelectedIndex;
        RecipientCombo.SelectedIndex = (index + delta + count) % count;
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.IsReady) _app.ShowWelcome();
        else _app.ShowSettings(SettingsWindow.PeopleTab);
    }

    private void SetStatus(string symbol, string brushKey, string text)
    {
        StatusDot.Text = symbol;
        StatusDot.Foreground = (Brush)FindResource(brushKey);
        StatusText.Text = text;
    }

    private void OnSendProgress(string id, long done, long total)
    {
        if (!_lastSend.ContainsKey(id) || total <= 0) return;
        ShowFeedback(done >= total ? "Archivo enviado" : $"Enviando archivo… {done * 100 / total} %", "MutedBrush", sticky: true);
    }

    private void OnDeliveryChanged(string id, DeliveryState state)
    {
        if (!_lastSend.ContainsKey(id)) return;
        _lastSend[id] = state;
        var confirm = _app.Settings.ConfirmDelivery;

        if (_lastSend.Count == 1 && _lastKinds.TryGetValue(id, out var kind) && kind != SentKind.Text)
        {
            var file = kind == SentKind.File;
            switch (state)
            {
                case DeliveryState.Sent:
                    ShowFeedback(file ? "Archivo ofrecido…" : "Enviando imagen…", "MutedBrush", sticky: true);
                    break;
                case DeliveryState.Delivered:
                    ShowFeedback(file ? "Archivo ofrecido ✓" : "Imagen entregada ✓", "OkBrush", sticky: file || confirm);
                    break;
                case DeliveryState.Shown:
                    ShowFeedback(file ? "Descargado ✓" : "Vista ✓", "OkBrush", sticky: false);
                    break;
                case DeliveryState.Declined:
                    ShowFeedback("Lo cerró sin descargar", "MutedBrush", sticky: false);
                    break;
                case DeliveryState.Failed:
                    ShowFeedback(file ? "No se pudo enviar el archivo" : "No se pudo enviar la imagen", "ErrBrush", sticky: false);
                    break;
            }
            return;
        }

        if (_lastSend.Count == 1)
        {
            switch (state)
            {
                case DeliveryState.Queued:
                    ShowFeedback("En espera de conexión…", "MutedBrush", sticky: true);
                    break;
                case DeliveryState.Sent:
                    ShowFeedback("Enviado", "MutedBrush", sticky: confirm);
                    break;
                case DeliveryState.Delivered:
                    ShowFeedback(confirm ? "Entregado ✓" : "Enviado ✓", "OkBrush", sticky: false);
                    break;
                case DeliveryState.Shown:
                    if (confirm) ShowFeedback("Visto ✓", "OkBrush", sticky: false);
                    break;
                case DeliveryState.Failed:
                    ShowFeedback("No entregado", "ErrBrush", sticky: false);
                    break;
            }
            return;
        }

        // Envío a varias personas: se resume ("Entregado a 3 de 4").
        var total = _lastSend.Count;
        var states = _lastSend.Values.ToList();
        var shown = states.Count(s => s == DeliveryState.Shown);
        var delivered = states.Count(s => s is DeliveryState.Delivered or DeliveryState.Shown);
        var failed = states.Count(s => s is DeliveryState.Failed or DeliveryState.Declined);
        var settled = delivered + failed == total;
        if (confirm && shown > 0)
            ShowFeedback(shown == total ? "Visto por todos ✓" : $"Visto por {shown} de {total}", "OkBrush", sticky: shown < total && !settled);
        else if (delivered > 0)
            ShowFeedback(delivered == total ? (confirm ? "Entregado a todos ✓" : "Enviado ✓") : $"Entregado a {delivered} de {total}",
                "OkBrush", sticky: !settled || (confirm && delivered == total));
        else if (failed == total)
            ShowFeedback("No entregado", "ErrBrush", sticky: false);
        else if (states.Any(s => s == DeliveryState.Queued))
            ShowFeedback("En espera de conexión…", "MutedBrush", sticky: true);
        else
            ShowFeedback("Enviado", "MutedBrush", sticky: confirm);
    }

    private void ShowFeedback(string text, string brushKey, bool sticky)
    {
        Feedback.Text = text;
        Feedback.Foreground = (Brush)FindResource(brushKey);
        _feedbackTimer.Stop();
        if (!sticky) _feedbackTimer.Start();
    }

    // ------------------------------------------------------------------ envío

    private void Send()
    {
        var hasContent = !string.IsNullOrWhiteSpace(Input.Text) || _attachments.Count > 0;
        if (RecipientCombo.SelectedItem is not RecipientOption to)
        {
            if (_app.IsReady && hasContent) ShowFeedback("Elegí a quién enviarlo", "ErrBrush", sticky: false);
            return;
        }
        var result = _app.SendMessage(to.Id, Input.Text, UrgentToggle.IsChecked == true, _attachments.ToList());
        if (!result.Accepted)
        {
            if (hasContent) ShowFeedback(result.Error ?? "No se pudo enviar", "ErrBrush", sticky: false);
            return;
        }
        _lastSend.Clear();
        _lastKinds.Clear();
        foreach (var (id, kind) in result.Sent)
        {
            _lastSend[id] = null;
            _lastKinds[id] = kind;
        }
        _app.RememberRecipient(to.Id);
        Input.Clear();
        UrgentToggle.IsChecked = false;
        _attachments.Clear();
        RenderAttachments();
        if (result.Warning != null) ShowFeedback(result.Warning, "WarnBrush", sticky: false);
        else if (result.AnyOnline) ShowFeedback("Enviando…", "MutedBrush", sticky: true);
        FocusInput();
        _app.AfterSend(result.AnyOnline);
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _app.HideMain();
        }
        else if (e.Key is Key.I or Key.U && Keyboard.Modifiers == ModifierKeys.Control) // Ctrl+I (Ctrl+U por costumbre)
        {
            e.Handled = true;
            UrgentToggle.IsChecked = UrgentToggle.IsChecked != true;
        }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            MoveRecipient(e.Key == Key.Down ? 1 : -1);
        }
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var len = Input.Text.Length;
        Counter.Visibility = len >= MessageRules.MaxLength - 50 ? Visibility.Visible : Visibility.Collapsed;
        Counter.Text = $"{len}/{MessageRules.MaxLength}";
        UpdateTypingSignal();
    }

    // ------------------------------------------------------------------ barra de título

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_app.HasTray) _app.HideMain();
        else WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting) return;
        e.Cancel = true;
        if (_app.HasTray) _app.HideMain();
        else _ = _app.ExitAsync();
    }
}
