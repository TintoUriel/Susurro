using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Susurro.App.Services;

namespace Susurro.App.Views;

public partial class RemoteWindow : Window
{
    private RemoteSession? _remote;
    private int _remoteW, _remoteH;
    private int _frameCount;
    private bool _controlling;
    private readonly DispatcherTimer _fpsTimer;

    internal RemoteWindow(RemoteSession remote)
    {
        _remote = remote;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);
        TitleText.Text = $"Control remoto — {remote.PeerName}";
        StatusText.Text = remote.IsHost ? "Compartiendo tu pantalla…" : "Conectado — esperando imagen…";

        remote.FrameReceived += OnFrameReceived;
        remote.Ended += OnRemoteEnded;

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += (_, _) =>
        {
            FpsText.Text = _frameCount > 0 ? $"{_frameCount} fps" : "";
            _frameCount = 0;
        };
        _fpsTimer.Start();

        if (remote.IsHost)
            remote.StartHosting();
    }

    private void OnFrameReceived(int w, int h, byte[] jpeg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(jpeg);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                ScreenImage.Source = bmp;
                _remoteW = w;
                _remoteH = h;
                _frameCount++;
                ResText.Text = $"{w}×{h}";
                if (StatusText.Text != "Conectado")
                    StatusText.Text = "Conectado";
            }
            catch { }
        });
    }

    private void OnRemoteEnded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "La sesión remota terminó.";
            _remote = null;
        });
    }

    private (int x, int y)? MapToRemote(MouseEventArgs e)
    {
        if (_remoteW <= 0 || _remoteH <= 0) return null;
        var pos = e.GetPosition(ScreenImage);
        var imgW = ScreenImage.ActualWidth;
        var imgH = ScreenImage.ActualHeight;
        if (imgW <= 0 || imgH <= 0) return null;
        var x = (int)(pos.X / imgW * _remoteW);
        var y = (int)(pos.Y / imgH * _remoteH);
        if (x < 0 || y < 0 || x >= _remoteW || y >= _remoteH) return null;
        return (x, y);
    }

    private void ControlToggle_Changed(object sender, RoutedEventArgs e)
    {
        _controlling = ControlToggle.IsChecked == true;
        ScreenImage.Cursor = _controlling ? Cursors.Cross : Cursors.Arrow;
        StatusText.Text = _controlling ? "Controlando" : "Conectado — solo mirando";
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_controlling || _remote == null || _remote.IsHost) return;
        if (MapToRemote(e) is var (x, y))
            _remote.SendMouseMove(x, y);
    }

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controlling || _remote == null || _remote.IsHost) return;
        if (MapToRemote(e) is var (x, y))
        {
            int btn = e.ChangedButton switch
            {
                MouseButton.Left => 0,
                MouseButton.Right => 1,
                MouseButton.Middle => 2,
                _ => -1,
            };
            if (btn >= 0) _remote.SendMouseButton(x, y, btn, true);
        }
        ScreenImage.CaptureMouse();
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_controlling || _remote == null || _remote.IsHost) return;
        if (MapToRemote(e) is var (x, y))
        {
            int btn = e.ChangedButton switch
            {
                MouseButton.Left => 0,
                MouseButton.Right => 1,
                MouseButton.Middle => 2,
                _ => -1,
            };
            if (btn >= 0) _remote.SendMouseButton(x, y, btn, false);
        }
        ScreenImage.ReleaseMouseCapture();
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_controlling || _remote == null || _remote.IsHost) return;
        _remote.SendMouseWheel(e.Delta);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (!_controlling || _remote == null || _remote.IsHost) return;
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk > 0)
        {
            _remote.SendKey(vk, true);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_controlling || _remote == null || _remote.IsHost) return;
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk > 0)
        {
            _remote.SendKey(vk, false);
            e.Handled = true;
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteW > 0 && _remoteH > 0)
        {
            WindowState = WindowState.Normal;
            Width = Math.Min(_remoteW + 24, SystemParameters.WorkArea.Width);
            Height = Math.Min(_remoteH + 32 + 28 + 16, SystemParameters.WorkArea.Height);
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e) =>
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _fpsTimer.Stop();
        if (_remote != null)
        {
            _remote.FrameReceived -= OnFrameReceived;
            _remote.Ended -= OnRemoteEnded;
            _remote.Stop();
            _remote = null;
        }
    }
}
