using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Susurro.App.Services;
using Susurro.Core.Messaging;
using Susurro.Core.Net;

namespace Susurro.App.Views;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly DispatcherTimer _feedbackTimer;
    private string? _lastMessageId;
    private bool _positioned;

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
        _app.PeerChanged += () => ApplyState(_app.Link.State);
        ApplyState(_app.Link.State);

        Loaded += (_, _) => PlaceWindow();
        Activated += (_, _) => FocusInput();
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
        var peer = _app.Settings.Peer;
        PairButton.Visibility = peer == null ? Visibility.Visible : Visibility.Collapsed;
        PeerLabel.Text = peer == null ? "Sin PC vinculada" : "Para: " + (state.PeerName ?? peer.Name);

        switch (state.Status)
        {
            case LinkStatus.Connected:
                SetStatus("●", "OkBrush", "Conectado");
                break;
            case LinkStatus.Connecting:
                SetStatus("○", "WarnBrush", "Conectando…");
                break;
            case LinkStatus.NotPaired:
                SetStatus("○", "FaintBrush", "Sin vincular");
                break;
            default:
                SetStatus("×", "ErrBrush", "Desconectado");
                break;
        }

        StatusDetail.Text = state.Detail ?? "";
        StatusDetail.Visibility = string.IsNullOrEmpty(state.Detail) ? Visibility.Collapsed : Visibility.Visible;
        SendButton.IsEnabled = peer != null;
    }

    private void SetStatus(string symbol, string brushKey, string text)
    {
        StatusDot.Text = symbol;
        StatusDot.Foreground = (Brush)FindResource(brushKey);
        StatusText.Text = text;
    }

    private void OnDeliveryChanged(string id, DeliveryState state)
    {
        if (id != _lastMessageId) return;
        var confirm = _app.Settings.ConfirmDelivery;
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
        var result = _app.SendMessage(Input.Text, UrgentToggle.IsChecked == true);
        if (!result.Accepted)
        {
            if (!string.IsNullOrWhiteSpace(Input.Text)) ShowFeedback(result.Error ?? "No se pudo enviar", "ErrBrush", sticky: false);
            return;
        }
        _lastMessageId = result.MessageId;
        Input.Clear();
        UrgentToggle.IsChecked = false;
        if (_app.Link.State.Status == LinkStatus.Connected) ShowFeedback("Enviando…", "MutedBrush", sticky: true);
        FocusInput();
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
        else if (e.Key == Key.U && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            UrgentToggle.IsChecked = UrgentToggle.IsChecked != true;
        }
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var len = Input.Text.Length;
        Counter.Visibility = len >= MessageRules.MaxLength - 50 ? Visibility.Visible : Visibility.Collapsed;
        Counter.Text = $"{len}/{MessageRules.MaxLength}";
    }

    // ------------------------------------------------------------------ barra de título

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private void Pair_Click(object sender, RoutedEventArgs e) => _app.ShowPairing();

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
