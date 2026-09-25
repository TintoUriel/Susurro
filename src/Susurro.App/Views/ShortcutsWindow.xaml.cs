using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Susurro.App.Services;

namespace Susurro.App.Views;

/// <summary>Resumen de los atajos de teclado y del mouse (F1 en la ventana principal, o desde la bandeja).</summary>
public partial class ShortcutsWindow : Window
{
    private readonly AppController _app;

    internal ShortcutsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);

        BuildGlobalRows();
        _app.HotkeyChanged += BuildGlobalRows;
        Closed += (_, _) => _app.HotkeyChanged -= BuildGlobalRows;

        Add(ComposeRows, "Enviar (si abriste con el atajo, volvés a lo que estabas)", "Enter");
        Add(ComposeRows, "Marcar como importante: queda en pantalla hasta que le hagan clic", "Ctrl", "I");
        Add(ComposeRows, "Cambiar a quién le escribís", "Ctrl", "↑ / ↓");
        Add(ComposeRows, "Pegar texto, una imagen o archivos copiados", "Ctrl", "V");
        Add(ComposeRows, "Ocultar la ventana", "Esc");
        Add(ComposeRows, "Ver estos atajos", "F1");

        Add(IncomingRows, "Cerrar un mensaje importante (recién ahí le llega el «Visto»)", "Clic");
        Add(IncomingRows, "Los demás mensajes se van solos: no hace falta tocar nada", "—");

        Add(TrayRows, "Mostrar u ocultar Susurro", "Clic");
        Add(TrayRows, "Menú: configuración, atajos y salir", "Clic derecho");
    }

    private void BuildGlobalRows()
    {
        GlobalRows.Children.Clear();
        var gesture = _app.Hotkey;
        if (gesture != null && _app.HotkeyActive)
        {
            Add(GlobalRows, "Abrir Susurro para escribir. Otra vez: cerrarlo y volver a lo que estabas",
                gesture.Display().Split(" + ", StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            var text = gesture == null
                ? "Sin atajo global. Activalo con «Cambiar el atajo global…» para escribir desde cualquier programa."
                : $"{gesture.Display()} no está disponible (lo usa Windows u otro programa). Elegí otra combinación.";
            GlobalRows.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 4) });
        }
    }

    /// <summary>Una fila: descripción a la izquierda y las teclas como "teclas" dibujadas a la derecha.</summary>
    private void Add(Panel panel, string description, params string[] keys)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        });

        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
                caps.Children.Add(new TextBlock { Text = "+", Foreground = Brush("FaintBrush"), FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            caps.Children.Add(KeyCap(keys[i]));
        }
        Grid.SetColumn(caps, 1);
        row.Children.Add(caps);
        AutomationProperties.SetName(row, $"{string.Join(" + ", keys)}: {description}");
        panel.Children.Add(row);
    }

    private UIElement KeyCap(string key)
    {
        if (key == "—")
            return new TextBlock { Text = key, Foreground = Brush("FaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Background = Brush("InputBrush"),
            BorderBrush = Brush("BorderStrongBrush"),
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 26,
            Child = new TextBlock
            {
                Text = key,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            e.Handled = true; // ya está abierta
        }
    }

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        _app.ShowSettings(SettingsWindow.GeneralTab);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
