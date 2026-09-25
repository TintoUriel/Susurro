using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Susurro.App.Services;
using Susurro.Core.Config;

namespace Susurro.App.Views;

/// <summary>
/// Primera vez: se pide el nombre de la persona (nunca se usa el nombre del equipo).
/// Hasta completarla, Susurro no sale a la red.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly AppController _app;

    internal WelcomeWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);

        // Si había un nombre elegido en una versión anterior, se ofrece para confirmarlo.
        NameBox.Text = app.Settings.FriendlyName;
        NameBox.SelectAll();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NameBox.Focus());
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        StartButton.IsEnabled = SettingsValidator.CleanName(NameBox.Text).Length > 0;
        Hint.Text = "";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.CompleteSetup(NameBox.Text))
        {
            Hint.Text = "Escribí tu nombre para empezar.";
            NameBox.Focus();
            return;
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
