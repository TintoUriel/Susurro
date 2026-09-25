using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Susurro.App.Overlay;
using Susurro.App.Services;
using Susurro.Core.Config;
using Susurro.Core.Messaging;
using Susurro.Core.Net;

namespace Susurro.App.Views;

public partial class SettingsWindow : Window
{
    public const int PeopleTab = 3;

    private readonly AppController _app;
    private readonly CancellationTokenSource _cts = new();
    private readonly AppSettings _edit;
    private bool _loading = true;
    private bool _previewUrgent;
    private string _hotkeyText = "";
    private bool _hotkeyCapturing;

    internal SettingsWindow(AppController app)
    {
        _app = app;
        _edit = app.Settings.Clone();
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);

        FillCombos();
        LoadValues();
        _loading = false;
        UpdatePreview();
        ApplyLinkState(_app.Link.State);
        _app.LinkStateChanged += ApplyLinkState;
        _app.ContactsChanged += RefreshContacts;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _app.LinkStateChanged -= ApplyLinkState;
            _app.ContactsChanged -= RefreshContacts;
            _app.ResumeHotkey(); // por si se cerró mientras se elegía un atajo
        };
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    // ------------------------------------------------------------------ carga

    private static ComboBoxItem Item(string text, object tag) => new() { Content = text, Tag = tag };

    private void FillCombos()
    {
        MonitorCombo.Items.Add(Item("Automático (monitor en uso)", OverlaySettings.MonitorAuto));
        MonitorCombo.Items.Add(Item("Monitor principal", OverlaySettings.MonitorPrimary));
        foreach (var m in MonitorService.GetMonitors())
            MonitorCombo.Items.Add(Item(m.Describe(), m.DeviceName));

        PositionCombo.Items.Add(Item("Abajo, centrado (subtítulo)", OverlayPosition.BottomCenter));
        PositionCombo.Items.Add(Item("Arriba, centrado", OverlayPosition.TopCenter));
        PositionCombo.Items.Add(Item("Centro de la pantalla", OverlayPosition.Center));
        PositionCombo.Items.Add(Item("Abajo a la izquierda", OverlayPosition.BottomLeft));
        PositionCombo.Items.Add(Item("Abajo a la derecha", OverlayPosition.BottomRight));
        PositionCombo.Items.Add(Item("Arriba a la izquierda", OverlayPosition.TopLeft));
        PositionCombo.Items.Add(Item("Arriba a la derecha", OverlayPosition.TopRight));

        foreach (var d in OverlaySettings.AllowedDurations)
            DurationCombo.Items.Add(Item($"{d} segundos", d));

        UrgentCombo.Items.Add(Item("Borde y etiqueta ámbar", UrgentStyle.Accent));
        UrgentCombo.Items.Add(Item("Texto en negrita", UrgentStyle.Bold));
        UrgentCombo.Items.Add(Item("Fondo cálido tenue", UrgentStyle.Tinted));

        ColorCombo.Items.Add(Item("Blanco", SubtitleColor.White));
        ColorCombo.Items.Add(Item("Amarillo (subtítulo clásico)", SubtitleColor.Yellow));
        ColorCombo.Items.Add(Item("Gris claro", SubtitleColor.LightGray));
    }

    private static void Select(ComboBox combo, object value, Func<object, string>? describeMissing = null)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag, value) || item.Tag is string s && value is string v && string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (describeMissing != null)
        {
            var extra = Item(describeMissing(value), value);
            combo.Items.Add(extra);
            combo.SelectedItem = extra;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void LoadValues()
    {
        NameBox.Text = _edit.FriendlyName;
        AutoStartBox.IsChecked = AutoStart.IsEnabled(_app.Args.Profile) || _edit.StartWithWindows;
        StartMinBox.IsChecked = _edit.StartMinimized;
        TrayBox.IsChecked = _edit.ShowTrayIcon;
        ConfirmBox.IsChecked = _edit.ConfirmDelivery;
        _hotkeyText = _edit.SendHotkey;
        ShowHotkey();

        var o = _edit.Overlay;
        Select(MonitorCombo, o.Monitor, v => $"{v} (no conectado)");
        Select(PositionCombo, o.Position);
        Select(DurationCombo, o.DurationSeconds, v => $"{v} segundos");
        Select(UrgentCombo, o.UrgentStyle);
        Select(ColorCombo, o.TextColor);
        OutlineBox.IsChecked = o.TextOutline;
        BackgroundBox.IsChecked = o.ShowBackground;
        OpacitySlider.IsEnabled = o.ShowBackground;
        FontSlider.Value = Math.Round(o.FontSize);
        OpacitySlider.Value = Math.Round(o.Opacity * 100);
        WidthSlider.Value = o.MaxWidthPercent;
        MarginSlider.Value = o.EdgeMarginPercent;
        SenderBox.IsChecked = o.ShowSenderName;
        AnimBox.IsChecked = o.Animations;
        ContrastBox.IsChecked = o.HighContrast;
        UpdateSliderLabels();

        PortBox.Text = _edit.Port.ToString(CultureInfo.InvariantCulture);
        PortHint.Text = $"UDP {_edit.DiscoveryPort} (descubrimiento)";
        LocalInfo.Text = $"Equipo {Environment.MachineName} · IP {NetworkInfo.DescribeLocalAddresses()} (para agregarte por dirección desde otra PC)";
        AboutText.Text = $"Susurro {AppController.Version} · id de instalación {_edit.InstanceId[..8]}…" +
                         (_app.Args.Profile != null ? $" · perfil «{_app.Args.Profile}»" : "") +
                         $"\nDatos: {_app.DataDirectory}";
        RefreshContacts();
    }

    // ------------------------------------------------------------------ personas

    private void RefreshContacts()
    {
        var selected = (ContactsList.SelectedItem as ListBoxItem)?.Tag as string;
        var contacts = _app.Link.Contacts;
        ContactsList.Items.Clear();
        foreach (var c in contacts)
        {
            var item = BuildContactItem(c);
            ContactsList.Items.Add(item);
            if (c.Id == selected) ContactsList.SelectedItem = item;
        }
        ContactsEmpty.Text = !_app.IsReady
            ? "Elegí tu nombre para empezar a buscar compañeros."
            : contacts.Count == 0
                ? "Todavía no apareció nadie. Verificá que Susurro esté abierto en las otras PCs y que estén en la misma red."
                : "";
        ContactsEmpty.Visibility = ContactsEmpty.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateContactButtons();
    }

    private ListBoxItem BuildContactItem(ContactInfo c)
    {
        var (dot, brush, status) = c.Blocked
            ? ("⊘", "ErrBrush", "bloqueado")
            : c.Status switch
            {
                ContactStatus.Online => ("●", "OkBrush", "conectado"),
                ContactStatus.Connecting => ("○", "WarnBrush", "conectando…"),
                _ => ("○", "FaintBrush", "desconectado"),
            };
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(dot + "  ") { Foreground = (Brush)FindResource(brush), FontSize = 11 });
        text.Inlines.Add(new Run(c.Name) { FontWeight = FontWeights.SemiBold });
        var extra = status + (c.Address != null ? " · " + c.Address : "") + (c.Detail != null ? " · " + c.Detail : "");
        text.Inlines.Add(new Run("  " + extra) { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11 });
        return new ListBoxItem { Content = text, Tag = c.Id, ToolTip = $"id {c.Id[..8]}…" };
    }

    private ContactInfo? SelectedContact() =>
        (ContactsList.SelectedItem as ListBoxItem)?.Tag is string id ? _app.Link.FindContact(id) : null;

    private void UpdateContactButtons()
    {
        var c = SelectedContact();
        BlockButton.IsEnabled = c != null;
        BlockButton.Content = c?.Blocked == true ? "Desbloquear" : "Bloquear";
        // Quitar solo tiene sentido con PCs desconectadas: una conectada volvería a aparecer al instante.
        ForgetButton.IsEnabled = c != null && c.Status != ContactStatus.Online;
    }

    private void ContactsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateContactButtons();

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedContact();
        if (c == null) return;
        if (!c.Blocked)
        {
            var answer = MessageBox.Show(this,
                $"¿Bloquear a «{c.Name}»?\n\nNo vas a recibir sus mensajes ni va a poder conectarse con esta PC hasta que lo desbloquees.",
                "Susurro", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        _app.SetBlocked(c.Id, !c.Blocked);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedContact();
        if (c == null) return;
        _app.ForgetContact(c.Id);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _app.Link.ReconnectNow();

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AddAsync();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _ = AddAsync();

    private async Task AddAsync()
    {
        var address = AddressBox.Text.Trim();
        if (!_app.IsReady)
        {
            SetAddStatus("Primero elegí tu nombre.", "ErrBrush");
            return;
        }
        if (address.Length == 0)
        {
            SetAddStatus("Escribí la IP o el nombre del equipo de la otra PC.", "ErrBrush");
            AddressBox.Focus();
            return;
        }
        AddButton.IsEnabled = false;
        SetAddStatus("Conectando…", "MutedBrush");
        try
        {
            var r = await _app.AddContactAsync(address, _cts.Token);
            if (r.Success && r.Contact != null)
            {
                SetAddStatus($"✓ Conectado con «{r.Contact.Name}».", "OkBrush");
                AddressBox.Clear();
            }
            else SetAddStatus(r.Error ?? "No se pudo conectar.", "ErrBrush");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddStatus("Error inesperado: " + ex.Message, "ErrBrush");
        }
        finally
        {
            if (IsLoaded) AddButton.IsEnabled = true;
        }
    }

    private void SetAddStatus(string text, string brushKey)
    {
        AddStatus.Text = text;
        AddStatus.Foreground = (Brush)FindResource(brushKey);
    }

    private void ApplyLinkState(LinkState state)
    {
        var (symbol, brush, text) = !_app.IsReady
            ? ("○", "FaintBrush", "Sin nombre todavía: no se busca a nadie")
            : state.Status switch
            {
                LinkStatus.Online => ("●", "OkBrush", $"{state.Online} de {state.Known} conectado{(state.Known == 1 ? "" : "s")}"),
                LinkStatus.NoneOnline => ("○", "FaintBrush", "Nadie conectado ahora — se reconectan solos al abrir Susurro"),
                _ => ("○", "WarnBrush", "Buscando compañeros en la red…"),
            };
        ConnDot.Text = symbol;
        ConnDot.Foreground = (Brush)FindResource(brush);
        ConnText.Text = text;
        ConnDetail.Text = state.Detail ?? "";
        ConnDetail.Visibility = string.IsNullOrEmpty(state.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ lectura de controles

    private OverlaySettings ReadOverlay()
    {
        var o = _edit.Overlay.Clone();
        o.Monitor = (MonitorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OverlaySettings.MonitorAuto;
        o.Position = (PositionCombo.SelectedItem as ComboBoxItem)?.Tag is OverlayPosition p ? p : OverlayPosition.BottomCenter;
        o.DurationSeconds = (DurationCombo.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 5;
        o.UrgentStyle = (UrgentCombo.SelectedItem as ComboBoxItem)?.Tag is UrgentStyle u ? u : UrgentStyle.Accent;
        o.FontSize = FontSlider.Value;
        o.Opacity = OpacitySlider.Value / 100.0;
        o.MaxWidthPercent = (int)WidthSlider.Value;
        o.EdgeMarginPercent = (int)MarginSlider.Value;
        o.ShowSenderName = SenderBox.IsChecked == true;
        o.Animations = AnimBox.IsChecked == true;
        o.HighContrast = ContrastBox.IsChecked == true;
        o.TextColor = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag is SubtitleColor c ? c : SubtitleColor.White;
        o.TextOutline = OutlineBox.IsChecked == true;
        o.ShowBackground = BackgroundBox.IsChecked == true;
        return o;
    }

    private void UpdateSliderLabels()
    {
        // Durante InitializeComponent los sliders disparan ValueChanged antes de que existan las etiquetas.
        if (FontValue == null || OpacityValue == null || WidthValue == null || MarginValue == null) return;
        FontValue.Text = $"{FontSlider.Value:0} px";
        OpacityValue.Text = $"{OpacitySlider.Value:0} %";
        WidthValue.Text = $"{WidthSlider.Value:0} %";
        MarginValue.Text = $"{MarginSlider.Value:0} %";
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        UpdatePreview();
    }

    // ------------------------------------------------------------------ atajo de teclado

    private void ShowHotkey(string? message = null, bool error = false)
    {
        HotkeyGesture.TryParse(_hotkeyText, out var g);
        HotkeyBox.Text = g?.Display() ?? "";
        string hint;
        if (message != null) hint = message;
        else if (_hotkeyCapturing) hint = "Apretá la combinación (por ejemplo Ctrl + Shift + Espacio). Esc cancela.";
        else if (g == null) hint = "Desactivado.";
        else if (_hotkeyText == _app.Settings.SendHotkey && !_app.HotkeyActive)
        {
            hint = "Ese atajo lo usa Windows u otro programa: elegí otro.";
            error = true;
        }
        else hint = "Abre Susurro con el cursor listo para escribir; Enter envía y te devuelve a lo que estabas haciendo.";
        HotkeyHint.Text = hint;
        HotkeyHint.Foreground = (Brush)FindResource(error ? "ErrBrush" : "MutedBrush");
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeyCapturing = true;
        _app.SuspendHotkey(); // para que la combinación actual llegue a este campo
        ShowHotkey();
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeyCapturing = false;
        _app.ResumeHotkey();
        if (HotkeyHint.Foreground != FindResource("ErrBrush")) ShowHotkey();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return;

        if (mods == ModifierKeys.None && key == Key.Escape)
        {
            Tabs.Focus();
            return;
        }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            _hotkeyText = "";
            Tabs.Focus();
            return;
        }
        if (HotkeyGesture.IsModifierKey(key))
        {
            // Mostrar lo que se va apretando: "Ctrl + Shift + …"
            var partial = new HotkeyGesture(mods, Key.None).Display();
            HotkeyBox.Text = partial.Replace("None", "…");
            return;
        }
        if (!HotkeyGesture.IsValid(mods, key))
        {
            ShowHotkey("Tiene que incluir Ctrl, Alt, Shift o Windows (por ejemplo Ctrl + Shift + Espacio).", error: true);
            return;
        }
        var gesture = new HotkeyGesture(mods, key);
        if (!_app.CanUseHotkey(gesture))
        {
            ShowHotkey($"{gesture.Display()} ya lo usa Windows u otro programa. Probá otra combinación.", error: true);
            return;
        }
        _hotkeyText = gesture.ToString();
        Tabs.Focus();
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyText = AppSettings.DefaultHotkey;
        ShowHotkey();
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyText = "";
        ShowHotkey();
    }

    // ------------------------------------------------------------------ apariencia / vista previa

    private void Appearance_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Background_Changed(object sender, RoutedEventArgs e)
    {
        if (OpacitySlider == null || OutlineBox == null) return;
        var on = BackgroundBox.IsChecked == true;
        OpacitySlider.IsEnabled = on;
        // Sin recuadro, el contorno es lo que garantiza que el texto se lea: se activa solo.
        if (!_loading && !on) OutlineBox.IsChecked = true;
        UpdatePreview();
    }

    private void PreviewUrgent_Click(object sender, RoutedEventArgs e)
    {
        _previewUrgent = !_previewUrgent;
        PreviewUrgentButton.Content = _previewUrgent ? "Ver mensaje normal en la vista previa" : "Ver importante en la vista previa";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_loading || PreviewHost == null) return;
        var text = _previewUrgent ? "VENÍ A LA OFICINA" : "Traé los papeles cuando puedas";
        var sender = _app.Settings.FriendlyName.Length > 0 ? _app.Settings.FriendlyName : "Susurro";
        var msg = new WhisperMessage("preview", text, sender, DateTimeOffset.UtcNow, _previewUrgent, 0, false, IsTest: true);
        var card = SubtitleVisual.Build(msg, ReadOverlay());
        card.MaxWidth = 560;
        PreviewHost.Content = card;
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    // ------------------------------------------------------------------ botones

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = SettingsValidator.CleanName(NameBox.Text);
        if (name.Length == 0)
        {
            Tabs.SelectedIndex = 0;
            SaveHint.Text = "Escribí tu nombre.";
            NameBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            Tabs.SelectedIndex = PeopleTab;
            SaveHint.Text = "El puerto debe estar entre 1024 y 65535.";
            PortBox.Focus();
            return;
        }
        if (port == _edit.DiscoveryPort)
        {
            Tabs.SelectedIndex = PeopleTab;
            SaveHint.Text = $"El puerto {port} lo usa el descubrimiento UDP.";
            PortBox.Focus();
            return;
        }
        _edit.FriendlyName = name;
        _edit.StartWithWindows = AutoStartBox.IsChecked == true;
        _edit.StartMinimized = StartMinBox.IsChecked == true;
        _edit.ShowTrayIcon = TrayBox.IsChecked == true;
        _edit.ConfirmDelivery = ConfirmBox.IsChecked == true;
        _edit.SendHotkey = _hotkeyText;
        _edit.Overlay = ReadOverlay();
        _edit.Port = port;

        _app.ApplySettings(_edit);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void TestNormal_Click(object sender, RoutedEventArgs e) => _app.ShowTestMessage(ReadOverlay(), urgent: false);

    private void TestUrgent_Click(object sender, RoutedEventArgs e) => _app.ShowTestMessage(ReadOverlay(), urgent: true);

    private void ViewLog_Click(object sender, RoutedEventArgs e) => _app.ShowLog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _app.OpenDataFolder();
}
