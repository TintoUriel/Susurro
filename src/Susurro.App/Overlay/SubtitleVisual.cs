using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Susurro.Core.Config;
using Susurro.Core.Messaging;

namespace Susurro.App.Overlay;

/// <summary>
/// Construye el "subtítulo" (recuadro + remitente + texto) según la apariencia configurada.
/// Lo usan el overlay real y la vista previa de Configuración, así ambos se ven idénticos.
/// </summary>
internal static class SubtitleVisual
{
    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    public static Color TextColorOf(SubtitleColor c) => c switch
    {
        SubtitleColor.Yellow => Color.FromRgb(0xF5, 0xE0, 0x3C),
        SubtitleColor.LightGray => Color.FromRgb(0xC8, 0xCB, 0xD0),
        _ => Color.FromRgb(0xF4, 0xF5, 0xF6),
    };

    public static Border Build(WhisperMessage message, OverlaySettings s)
    {
        var hc = s.HighContrast || SystemParameters.HighContrast;
        var urgent = message.Urgent;
        // En alto contraste siempre hay recuadro opaco: la legibilidad manda.
        var background = s.ShowBackground || hc;
        var alpha = (byte)Math.Round(255 * (hc ? Math.Max(0.95, s.Opacity) : s.Opacity));

        // Los importantes y las imágenes se cierran con un clic: sin recuadro se usa un fondo casi
        // invisible (alfa 1) para que toda la tarjeta reciba el clic y no solo las letras.
        var clickable = urgent || message.IsImage;
        Brush bg = !background ? (clickable ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent)
            : hc ? new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))
            : urgent && s.UrgentStyle == UrgentStyle.Tinted ? new SolidColorBrush(Color.FromArgb(alpha, 38, 27, 16))
            : new SolidColorBrush(Color.FromArgb(alpha, 17, 18, 21));
        Brush border = !background ? Brushes.Transparent
            : hc ? Brushes.White
            : urgent && s.UrgentStyle == UrgentStyle.Accent ? new SolidColorBrush(Color.FromArgb(0xD0, 0xE0, 0xA4, 0x58))
            : new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        var borderThickness = !background ? 0 : hc ? 2.0 : urgent && s.UrgentStyle == UrgentStyle.Accent ? 1.5 : 1.0;

        var textColor = hc ? (s.TextColor == SubtitleColor.Yellow ? Colors.Yellow : Colors.White) : TextColorOf(s.TextColor);
        var labelColor = hc ? (urgent ? Colors.Yellow : Color.FromRgb(0xDD, 0xDD, 0xDD))
            : urgent && s.UrgentStyle != UrgentStyle.Bold ? Color.FromRgb(0xE0, 0xA4, 0x58)
            : !background ? Color.FromRgb(0xD0, 0xD3, 0xD8) // sin recuadro, el gris apagado se perdería
            : Color.FromRgb(0xA4, 0xA8, 0xB0);

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        string? label = null;
        var kind = urgent ? "importante" : message.IsImage ? "imagen" : null;
        if (s.ShowSenderName && !string.IsNullOrWhiteSpace(message.SenderName))
            label = kind != null ? $"{message.SenderName} · {kind}" : message.SenderName;
        else
            label = kind;

        if (label != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Foreground = new SolidColorBrush(labelColor),
                FontFamily = Font,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Tag = "label",
            });
        }

        if (message.IsImage)
        {
            var source = DecodeImage(message.Image!, 2560);
            panel.Children.Add(source != null
                ? new Image
                {
                    Source = source,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = "image",
                }
                : new TextBlock
                {
                    Text = "(no se pudo mostrar la imagen)",
                    Foreground = new SolidColorBrush(labelColor),
                    FontFamily = Font,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = "label",
                });
        }

        var text = new TextBlock
        {
            Text = message.Text,
            Foreground = new SolidColorBrush(textColor),
            FontFamily = Font,
            FontWeight = urgent ? (s.UrgentStyle == UrgentStyle.Bold || hc ? FontWeights.Bold : FontWeights.SemiBold)
                : background ? FontWeights.Normal : FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = "text",
        };
        if (!message.IsImage || message.Text.Length > 0) panel.Children.Add(text);

        if (message.IsImage)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Tag = "actions" };
            actions.Children.Add(new Button { Content = "Guardar", Tag = "save", Focusable = false, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) });
            actions.Children.Add(new Button { Content = "Cerrar", Tag = "close", Focusable = false, MinWidth = 90 });
            panel.Children.Add(actions);
        }
        else if (urgent)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Clic para cerrar",
                Foreground = new SolidColorBrush(labelColor) { Opacity = 0.85 },
                FontFamily = Font,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Tag = "hint",
            });
        }

        if (s.TextOutline && !hc)
        {
            // Contorno: halo oscuro alrededor de las letras + leve sombra desplazada (dos efectos anidados).
            text.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 5, Opacity = 1, RenderingBias = RenderingBias.Quality };
            panel.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 1.5, Direction = 300, BlurRadius = 3, Opacity = 0.9 };
        }

        var card = new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(borderThickness),
            CornerRadius = new CornerRadius(10),
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(card, TextRenderingMode.Grayscale); // ventanas en capas: sin ClearType
        ApplyFontSize(card, s.FontSize);
        return card;
    }

    /// <summary>
    /// Decodifica una imagen recibida de la red de forma segura: solo PNG/JPEG, dimensiones razonables
    /// (se leen de la cabecera antes de decodificar) y ancho acotado al decodificar (memoria acotada).
    /// </summary>
    public static BitmapSource? DecodeImage(byte[] data, int maxPixelWidth)
    {
        var isPng = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
        var isJpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;
        if (!isPng && !isJpeg) return null;
        try
        {
            var header = BitmapFrame.Create(new MemoryStream(data, false), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int w = header.PixelWidth, h = header.PixelHeight;
            if (w <= 0 || h <= 0 || w > 20000 || h > 20000 || (long)w * h > 100_000_000) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data, false);
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (w > maxPixelWidth) image.DecodePixelWidth = maxPixelWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    public static void ApplyFontSize(Border card, double size)
    {
        card.Padding = new Thickness(Math.Round(size * 1.05), Math.Round(size * 0.55), Math.Round(size * 1.05), Math.Round(size * 0.6));
        foreach (var child in ((StackPanel)card.Child).Children)
        {
            if (child is Image img)
            {
                img.Margin = new Thickness(0, Math.Round(size * 0.1), 0, Math.Round(size * 0.35));
                continue;
            }
            if (child is StackPanel { Tag: "actions" } actions)
            {
                actions.Margin = new Thickness(0, Math.Round(size * 0.45), 0, 0);
                foreach (var b in actions.Children.OfType<Button>()) b.FontSize = Math.Max(12, Math.Round(size * 0.55));
                continue;
            }
            if (child is not TextBlock tb) continue;
            if ((string)tb.Tag == "label")
            {
                tb.FontSize = Math.Max(10, Math.Round(size * 0.5));
                tb.Margin = new Thickness(0, 0, 0, Math.Round(size * 0.2));
            }
            else if ((string)tb.Tag == "hint")
            {
                tb.FontSize = Math.Max(10, Math.Round(size * 0.45));
                tb.Margin = new Thickness(0, Math.Round(size * 0.3), 0, 0);
            }
            else
            {
                tb.FontSize = size;
            }
        }
    }
}
