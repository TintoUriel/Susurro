using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private readonly AppController _app;
    private readonly AppSettings _edit;
    private bool _loading = true;
    private bool _previewUrgent;

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
        _app.PeerChanged += OnPeerChanged;
        Closed += (_, _) =>
        {
            _app.LinkStateChanged -= ApplyLinkState;
            _app.PeerChanged -= OnPeerChanged;
        };
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
        MachineInfo.Text = $"Equipo: {Environment.MachineName}";
        AutoStartBox.IsChecked = AutoStart.IsEnabled(_app.Args.Profile) || _edit.StartWithWindows;
        StartMinBox.IsChecked = _edit.StartMinimized;
        TrayBox.IsChecked = _edit.ShowTrayIcon;
        ConfirmBox.IsChecked = _edit.ConfirmDelivery;

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

        ManualBox.Text = _edit.Peer?.ManualAddress ?? "";
        PortBox.Text = _edit.Port.ToString(CultureInfo.InvariantCulture);
        PortHint.Text = $"UDP {_edit.DiscoveryPort} (descubrimiento)";
        LocalInfo.Text = $"{_edit.FriendlyName} · {Environment.MachineName} · IP {NetworkInfo.DescribeLocalAddresses()}";
        AboutText.Text = $"Susurro {AppController.Version} · id de instalación {_edit.InstanceId[..8]}…" +
                         (_app.Args.Profile != null ? $" · perfil «{_app.Args.Profile}»" : "") +
                         $"\nDatos: {_app.DataDirectory}";
        UpdatePeerSection();
    }

    private void UpdatePeerSection()
    {
        var peer = _app.Settings.Peer;
        if (peer == null)
        {
            PeerName.Text = "Ninguna";
            PeerInfo.Text = "Vinculá esta PC con la otra para poder enviar mensajes.";
            UnpairButton.IsEnabled = false;
            ReconnectButton.IsEnabled = false;
            ManualBox.IsEnabled = false;
            return;
        }
        PeerName.Text = peer.Name;
        var last = peer.LastAddress != null ? $"{peer.LastAddress}:{(peer.LastPort > 0 ? peer.LastPort : AppSettings.DefaultPort)}" : "desconocida";
        PeerInfo.Text = $"Última dirección: {last} · id {peer.InstanceId[..8]}… · vinculada el {peer.PairedUtc.ToLocalTime():d}";
        UnpairButton.IsEnabled = true;
        ReconnectButton.IsEnabled = true;
        ManualBox.IsEnabled = true;
    }

    private void OnPeerChanged() => UpdatePeerSection();

    private void ApplyLinkState(LinkState state)
    {
        var (symbol, brush, text) = state.Status switch
        {
            LinkStatus.Connected => ("●", "OkBrush", $"Conectado ({state.RemoteEndPoint})"),
            LinkStatus.Connecting => ("○", "WarnBrush", "Conectando…"),
            LinkStatus.NotPaired => ("○", "FaintBrush", "Sin vincular"),
            _ => ("×", "ErrBrush", "Desconectado — reintentando automáticamente"),
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
        PreviewUrgentButton.Content = _previewUrgent ? "Ver mensaje normal en la vista previa" : "Ver urgente en la vista previa";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_loading || PreviewHost == null) return;
        var text = _previewUrgent ? "VENÍ A LA OFICINA" : "Traé los papeles cuando puedas";
        var msg = new WhisperMessage("preview", text, _app.Settings.FriendlyName, DateTimeOffset.UtcNow, _previewUrgent, 0, false, IsTest: true);
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
            SaveHint.Text = "Escribí un nombre para esta PC.";
            NameBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            Tabs.SelectedIndex = 3;
            SaveHint.Text = "El puerto debe estar entre 1024 y 65535.";
            PortBox.Focus();
            return;
        }
        if (port == _edit.DiscoveryPort)
        {
            Tabs.SelectedIndex = 3;
            SaveHint.Text = $"El puerto {port} lo usa el descubrimiento UDP.";
            PortBox.Focus();
            return;
        }
        var manual = ManualBox.Text.Trim();
        if (manual.Length > 0 && !NetworkInfo.TryParseHostPort(manual, AppSettings.DefaultPort, out _, out _))
        {
            Tabs.SelectedIndex = 3;
            SaveHint.Text = "La dirección manual no es válida.";
            ManualBox.Focus();
            return;
        }

        _edit.FriendlyName = name;
        _edit.StartWithWindows = AutoStartBox.IsChecked == true;
        _edit.StartMinimized = StartMinBox.IsChecked == true;
        _edit.ShowTrayIcon = TrayBox.IsChecked == true;
        _edit.ConfirmDelivery = ConfirmBox.IsChecked == true;
        _edit.Overlay = ReadOverlay();
        _edit.Port = port;
        if (_edit.Peer != null) _edit.Peer.ManualAddress = manual.Length > 0 ? manual : null;

        _app.ApplySettings(_edit);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void TestNormal_Click(object sender, RoutedEventArgs e) => _app.ShowTestMessage(ReadOverlay(), urgent: false);

    private void TestUrgent_Click(object sender, RoutedEventArgs e) => _app.ShowTestMessage(ReadOverlay(), urgent: true);

    private void Reconnect_Click(object sender, RoutedEventArgs e) => _app.Link.ReconnectNow();

    private void Repair_Click(object sender, RoutedEventArgs e) => _app.ShowPairing();

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        var peer = _app.Settings.Peer;
        if (peer == null) return;
        var answer = MessageBox.Show(this,
            $"¿Desvincular de «{peer.Name}»?\n\nNo se podrán enviar ni recibir mensajes hasta volver a vincular ambas PCs.",
            "Susurro", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _app.Unpair();
        _edit.Peer = null;
        UpdatePeerSection();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e) => _app.ShowLog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _app.OpenDataFolder();
}
