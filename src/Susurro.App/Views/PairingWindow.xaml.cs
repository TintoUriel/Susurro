using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Susurro.App.Services;
using Susurro.Core.Config;
using Susurro.Core.Discovery;
using Susurro.Core.Net;
using Susurro.Core.Pairing;

namespace Susurro.App.Views;

/// <summary>
/// Vinculación de las dos PCs. Una PC "muestra el código"; la otra lo "ingresa" eligiendo la PC
/// encontrada en la red (o escribiendo su dirección). Ver docs/PAIRING.md.
/// </summary>
public partial class PairingWindow : Window
{
    private readonly AppController _app;
    private readonly bool _firstRun;
    private readonly DispatcherTimer _countdown;
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _openedUtc = DateTime.UtcNow;
    private PairingInvitation? _invitation;
    private bool _paired;
    private bool _searched;
    private bool _searching;
    private string? _addressFromList;

    internal PairingWindow(AppController app, bool firstRun)
    {
        _app = app;
        _firstRun = firstRun;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);

        Heading.Text = firstRun ? "Bienvenido a Susurro" : "Vincular PCs";
        DoneButton.Content = firstRun ? "Omitir por ahora" : "Cerrar";
        NameBox.Text = app.Settings.FriendlyName;
        LocalInfo.Text = $"Equipo {Environment.MachineName} · IP {NetworkInfo.DescribeLocalAddresses()} · puerto {app.Settings.Port}";
        if (app.Settings.Peer is { } current)
            FooterHint.Text = $"Vinculada ahora con «{current.Name}». Vincular de nuevo reemplaza ese vínculo.";

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => UpdateCountdown();

        _app.PeerChanged += OnPeerChanged;
        _app.InvitationClosed += OnInvitationClosed;
        Closed += OnClosed;
    }

    // ------------------------------------------------------------------ mostrar código

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        SaveName();
        _invitation = _app.Link.OpenPairing();
        CodeText.Text = PairingCode.Format(_invitation.Code);
        CodePanel.Visibility = Visibility.Visible;
        CodePanel.Opacity = 1;
        GenerateButton.Content = "Generar otro código";
        SetStatus(HostStatus, "Esperando a la otra PC…", "MutedBrush");
        UpdateCountdown();
        _countdown.Start();
    }

    private void UpdateCountdown()
    {
        if (_invitation == null) return;
        var remaining = _invitation.ExpiresUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _countdown.Stop();
            _app.Link.CancelPairing();
            CodePanel.Opacity = 0.45;
            CodeExpiry.Text = "Código vencido";
            SetStatus(HostStatus, "Generá un código nuevo para intentarlo otra vez.", "MutedBrush");
            _invitation = null;
            return;
        }
        CodeExpiry.Text = $"Vence en {(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    private void OnInvitationClosed()
    {
        if (_paired || _invitation == null || _app.Link.ActiveInvitation != null) return;
        _countdown.Stop();
        _invitation = null;
        CodePanel.Opacity = 0.45;
        CodeExpiry.Text = "Código invalidado";
        SetStatus(HostStatus, "Demasiados intentos con un código incorrecto. Generá uno nuevo.", "ErrBrush");
    }

    // ------------------------------------------------------------------ ingresar código

    private void Modes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Modes) return;
        if (Modes.SelectedIndex == 1 && !_searched) _ = SearchAsync();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        _searched = true;
        SearchButton.IsEnabled = false;
        SearchStatus.Text = "Buscando en la red…";
        try
        {
            var found = await _app.Link.DiscoverAsync(TimeSpan.FromSeconds(3), _cts.Token);
            FoundList.Items.Clear();
            foreach (var d in found.OrderByDescending(f => f.PairingOpen).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                FoundList.Items.Add(BuildItem(d));
            SearchStatus.Text = found.Count switch
            {
                0 => "No se encontró ninguna. Verificá que Susurro esté abierto en la otra PC, o escribí su dirección.",
                1 => "Se encontró 1 PC.",
                _ => $"Se encontraron {found.Count} PCs.",
            };
            var open = found.Where(f => f.PairingOpen).ToList();
            if (open.Count == 1)
                FoundList.SelectedItem = FoundList.Items.Cast<ListBoxItem>().First(i => ((DiscoveredInstance)i.Tag).InstanceId == open[0].InstanceId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "No se pudo buscar: " + ex.Message;
        }
        finally
        {
            _searching = false;
            if (IsLoaded) SearchButton.IsEnabled = true;
        }
    }

    private ListBoxItem BuildItem(DiscoveredInstance d)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(string.IsNullOrEmpty(d.Name) ? "(sin nombre)" : d.Name) { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run("  " + d.Address) { Foreground = (Brush)FindResource("MutedBrush") });
        if (d.PairingOpen)
            text.Inlines.Add(new Run("  · esperando código") { Foreground = (Brush)FindResource("AccentBrush"), FontSize = 11 });
        return new ListBoxItem { Content = text, Tag = d };
    }

    private void FoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoundList.SelectedItem is not ListBoxItem { Tag: DiscoveredInstance d }) return;
        var address = d.Port == AppSettings.DefaultPort ? d.Address.ToString() : $"{d.Address}:{d.Port}";
        AddressBox.Text = address;
        _addressFromList = address;
        CodeBox.Focus();
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = JoinAsync();
        }
    }

    private void Join_Click(object sender, RoutedEventArgs e) => _ = JoinAsync();

    private async Task JoinAsync()
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0)
        {
            SetStatus(JoinStatus, "Elegí una PC de la lista o escribí su dirección.", "ErrBrush");
            AddressBox.Focus();
            return;
        }
        if (PairingCode.Normalize(CodeBox.Text) == null)
        {
            SetStatus(JoinStatus, "El código tiene 8 caracteres (por ejemplo K7QM-4XPD).", "ErrBrush");
            CodeBox.Focus();
            return;
        }

        SaveName();
        JoinButton.IsEnabled = false;
        SetStatus(JoinStatus, "Vinculando…", "MutedBrush");
        try
        {
            // Si la dirección se escribió a mano, se recuerda como dirección manual.
            var remember = !string.Equals(address, _addressFromList, StringComparison.OrdinalIgnoreCase);
            var result = await _app.Link.JoinAsync(address, CodeBox.Text, remember, _cts.Token);
            if (result.Success && result.Peer != null) Success(result.Peer.Name);
            else SetStatus(JoinStatus, result.Error ?? "No se pudo vincular.", "ErrBrush");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus(JoinStatus, "Error inesperado: " + ex.Message, "ErrBrush");
        }
        finally
        {
            if (IsLoaded && !_paired) JoinButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ resultado

    private void OnPeerChanged()
    {
        var peer = _app.Settings.Peer;
        if (peer != null && peer.PairedUtc >= _openedUtc) Success(peer.Name);
    }

    private void Success(string peerName)
    {
        if (_paired) return;
        _paired = true;
        _countdown.Stop();
        _invitation = null;
        CodePanel.Visibility = Visibility.Collapsed;
        GenerateButton.IsEnabled = false;
        JoinButton.IsEnabled = false;
        var text = $"✓ Vinculado con «{peerName}». Ya pueden enviarse mensajes.";
        SetStatus(HostStatus, text, "OkBrush");
        SetStatus(JoinStatus, text, "OkBrush");
        FooterHint.Text = "";
        DoneButton.Content = "Listo";
        DoneButton.Style = (Style)FindResource("PrimaryButton");
        _app.CompleteSetup(NameBox.Text);
    }

    private void SetStatus(TextBlock target, string text, string brushKey)
    {
        target.Text = text;
        target.Foreground = (Brush)FindResource(brushKey);
    }

    private void SaveName()
    {
        var name = SettingsValidator.CleanName(NameBox.Text);
        if (name.Length > 0) _app.SetFriendlyName(name);
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => SaveName();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _countdown.Stop();
        _app.PeerChanged -= OnPeerChanged;
        _app.InvitationClosed -= OnInvitationClosed;
        if (!_paired && _app.Link.ActiveInvitation != null) _app.Link.CancelPairing();
        if (_firstRun) _app.CompleteSetup(NameBox.Text);
        else SaveName();
        _cts.Dispose();
    }
}
