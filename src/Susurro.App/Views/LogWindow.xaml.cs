using System.Windows;
using Susurro.App.Services;

namespace Susurro.App.Views;

/// <summary>Visor del registro para diagnóstico (lectura bajo demanda; no se actualiza solo).</summary>
public partial class LogWindow : Window
{
    private readonly AppController _app;

    internal LogWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);
        PathText.Text = app.LogSink.FilePath;
        Refresh();
    }

    private void Refresh()
    {
        LogText.Text = _app.LogSink.ReadAll();
        LogText.CaretIndex = LogText.Text.Length;
        LogText.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); } catch { }
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => _app.OpenDataFolder();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
