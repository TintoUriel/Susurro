using System.Windows;
using System.Windows.Input;
using Susurro.App.Services;

namespace Susurro.App.Views;

/// <summary>
/// Ventana del comando «/control»: abre Quick Assist (la asistencia remota de Microsoft) y te muestra
/// con quién vas a hablar. Susurro no captura pantallas ni controla nada por su cuenta; eso lo hace
/// Quick Assist, con su propio pedido de permiso.
/// </summary>
public partial class SupportWindow : Window
{
    private readonly AppController _app;

    internal SupportWindow(AppController app, string? personName, string? address)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);
        SetPerson(personName, address);
        Launch();
    }

    /// <summary>Actualiza con quién vas a hablar (cuando la ventana ya está abierta y cambia el destinatario).</summary>
    public void SetPerson(string? personName, string? address)
    {
        PersonText.Text = personName != null
            ? (string.IsNullOrWhiteSpace(address) ? $"Vas a ayudar a {personName}" : $"Vas a ayudar a {personName}  ·  {address}")
            : "Abrí Quick Assist para ayudar a alguien de la oficina.";
    }

    /// <summary>Abre Quick Assist y refleja el resultado (ofrece instalarlo si falta).</summary>
    public void Launch()
    {
        var result = RemoteSupport.LaunchQuickAssist();
        var ok = result == RemoteSupport.Result.Launched;
        StatusText.Text = ok
            ? "Quick Assist se está abriendo…"
            : "No encontré Quick Assist en esta PC. Instalalo desde la Microsoft Store y volvé a intentar.";
        InstallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Launch();

    private void Install_Click(object sender, RoutedEventArgs e) => RemoteSupport.OpenStore();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
