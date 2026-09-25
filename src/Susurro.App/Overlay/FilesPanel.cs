using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Susurro.App.Native;
using Susurro.Core.Config;
using Susurro.Core.Logging;
using Susurro.Core.Transfers;

namespace Susurro.App.Overlay;

/// <summary>
/// Tarjetas de archivos recibidos, arriba a la izquierda de la pantalla. Cada una: quién lo manda,
/// nombre y tamaño, y Descargar / Cerrar (luego progreso, Abrir / Mostrar en carpeta, o el error).
/// Como el overlay, es una ventana que no se activa ni toma el foco (WS_EX_NOACTIVATE), no aparece en
/// Alt+Tab y queda por encima; a diferencia del subtítulo, sí recibe clics. Se crea con la primera
/// tarjeta y se destruye con la última: sin archivos no existe ninguna ventana.
/// </summary>
internal sealed class FilesPanel : IDisposable
{
    private const double CardWidth = 330;

    private readonly Func<OverlaySettings> _settings;
    private readonly Dictionary<string, FileCard> _cards = new();
    private readonly StackPanel _stack = new();
    private HwndSource? _source;

    public FilesPanel(Func<OverlaySettings> settings) => _settings = settings;

    /// <summary>No hay tarjetas de archivos en pantalla.</summary>
    public bool IsEmpty => _cards.Count == 0;

    public event Action<string>? DownloadRequested;
    /// <summary>Cerrar la tarjeta (si estaba descargando, cancela).</summary>
    public event Action<string>? CloseRequested;
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public void Add(IncomingFile file)
    {
        if (_cards.ContainsKey(file.Id)) return;
        var card = new FileCard(this, file);
        _cards[file.Id] = card;
        _stack.Children.Insert(0, card.Root); // la más nueva arriba
        EnsureWindow();
    }

    public void SetDownloading(string id) => Get(id)?.SetDownloading();

    public void SetProgress(string id, long done, long total) => Get(id)?.SetProgress(done, total);

    public void SetCompleted(string id, string path) => Get(id)?.SetCompleted(path);

    public void SetFailed(string id, string reason) => Get(id)?.SetFailed(reason);

    public void Remove(string id)
    {
        if (!_cards.Remove(id, out var card)) return;
        _stack.Children.Remove(card.Root);
        if (_cards.Count == 0) CloseWindow();
    }

    public void Dispose()
    {
        _cards.Clear();
        _stack.Children.Clear();
        CloseWindow();
    }

    private FileCard? Get(string id) => _cards.TryGetValue(id, out var c) ? c : null;

    private void EnsureWindow()
    {
        if (_source != null) return;
        try
        {
            var monitor = MonitorService.Resolve(_settings().Monitor);
            var margin = (int)Math.Round(16 * monitor.Scale);
            var parameters = new HwndSourceParameters("SusurroArchivos")
            {
                WindowStyle = NativeMethods.WS_POPUP,
                ExtendedWindowStyle = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                UsesPerPixelTransparency = true,
                PositionX = monitor.WorkArea.Left + margin,
                PositionY = monitor.WorkArea.Top + margin,
                Width = 1,
                Height = 1,
            };
            _source = new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight };
            _source.AddHook(WndProc);
            var scale = _source.CompositionTarget?.TransformToDevice.M11 ?? monitor.Scale;
            var root = new ScrollViewer
            {
                Content = _stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = monitor.WorkArea.Height / scale * 0.8,
                Background = Brushes.Transparent,
                Focusable = false,
            };
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
            _source.RootVisual = root;
            NativeMethods.ShowWindow(_source.Handle, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Error("files", "No se pudo mostrar el panel de archivos", ex);
            CloseWindow();
        }
    }

    private void CloseWindow()
    {
        if (_source == null) return;
        try
        {
            _source.RemoveHook(WndProc);
            if (_source.RootVisual is ScrollViewer sv) sv.Content = null;
            _source.RootVisual = null;
            _source.Dispose();
        }
        catch
        {
        }
        _source = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Una tarjeta de archivo.</summary>
    private sealed class FileCard
    {
        private readonly FilesPanel _panel;
        private readonly IncomingFile _file;
        private readonly TextBlock _status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Grid _progress = new() { Height = 3, Margin = new Thickness(0, 9, 0, 0), Visibility = Visibility.Collapsed };
        private readonly ScaleTransform _fill = new(0, 1);
        private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 10, 0, 0) };
        private string? _path;

        public FileCard(FilesPanel panel, IncomingFile file)
        {
            _panel = panel;
            _file = file;

            _progress.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Res("BorderStrongBrush") });
            _progress.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Res("AccentBrush"), RenderTransform = _fill, RenderTransformOrigin = new Point(0, 0.5) });

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = $"{file.SenderName} te envió un archivo", FontSize = 11.5, Foreground = Res("MutedBrush") });
            info.Children.Add(new TextBlock
            {
                Text = file.Name,
                ToolTip = file.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            info.Children.Add(new TextBlock { Text = FileNames.FormatSize(file.Size), FontSize = 11.5, Foreground = Res("FaintBrush"), Margin = new Thickness(0, 1, 0, 0) });
            info.Children.Add(_progress);
            info.Children.Add(_status);
            info.Children.Add(_buttons);

            var icon = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 22,
                Foreground = Res("AccentBrush"),
                Margin = new Thickness(0, 4, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(icon);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            Root = new Border
            {
                Width = CardWidth,
                Background = Res("SurfaceBrush"),
                BorderBrush = Res("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            };
            System.Windows.Documents.TextElement.SetFontFamily(Root, (FontFamily)Application.Current.FindResource("UiFont"));
            SetButtons(("Descargar", true, () => _panel.DownloadRequested?.Invoke(_file.Id)),
                       ("Cerrar", false, () => _panel.CloseRequested?.Invoke(_file.Id)));
        }

        public Border Root { get; }

        public void SetDownloading()
        {
            _progress.Visibility = Visibility.Visible;
            _fill.ScaleX = 0;
            Status("Descargando…", "MutedBrush");
            SetButtons(("Cancelar", false, () => _panel.CloseRequested?.Invoke(_file.Id)));
        }

        public void SetProgress(long done, long total)
        {
            if (total <= 0) return;
            var f = Math.Clamp((double)done / total, 0, 1);
            _progress.Visibility = Visibility.Visible;
            _fill.ScaleX = f;
            Status($"Descargando… {f * 100:0} %", "MutedBrush");
        }

        public void SetCompleted(string path)
        {
            _path = path;
            _progress.Visibility = Visibility.Collapsed;
            Status($"✓ Guardado en Descargas como «{System.IO.Path.GetFileName(path)}»", "OkBrush");
            SetButtons(("Abrir", true, () => _panel.OpenRequested?.Invoke(_path!)),
                       ("Mostrar en carpeta", false, () => _panel.ShowInFolderRequested?.Invoke(_path!)),
                       ("Cerrar", false, () => _panel.CloseRequested?.Invoke(_file.Id)));
        }

        public void SetFailed(string reason)
        {
            _progress.Visibility = Visibility.Collapsed;
            Status(reason, "ErrBrush");
            SetButtons(("Reintentar", true, () => _panel.DownloadRequested?.Invoke(_file.Id)),
                       ("Cerrar", false, () => _panel.CloseRequested?.Invoke(_file.Id)));
        }

        private void Status(string text, string brush)
        {
            _status.Text = text;
            _status.Foreground = Res(brush);
            _status.Visibility = Visibility.Visible;
        }

        private void SetButtons(params (string Text, bool Primary, Action Click)[] buttons)
        {
            _buttons.Children.Clear();
            foreach (var (text, primary, click) in buttons)
            {
                var b = new Button
                {
                    Content = text,
                    Focusable = false, // un clic no debe mover el foco a esta ventana
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                if (primary) b.Style = (Style)Application.Current.FindResource("PrimaryButton");
                b.Click += (_, _) => click();
                _buttons.Children.Add(b);
            }
        }
    }
}
