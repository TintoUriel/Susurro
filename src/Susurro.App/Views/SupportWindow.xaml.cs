using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Susurro.App.Services;
using Susurro.Core.RemoteSupport;

namespace Susurro.App.Views;

/// <summary>
/// Ventana del comando «/control»: deja listo RustDesk (lo descarga y verifica la primera vez) y lo abre para
/// conectar con la persona elegida, mostrándote con quién vas a hablar. Susurro no captura pantallas ni controla
/// nada por su cuenta; eso lo hace RustDesk, con su propio pedido de permiso del lado de la otra persona.
/// </summary>
public partial class SupportWindow : Window
{
    private readonly AppController _app;
    private readonly CancellationTokenSource _cts = new();
    private string? _address;
    private bool _busy;
    private bool _closed;

    internal SupportWindow(AppController app, string? personName, string? address)
    {
        _app = app;
        InitializeComponent();
        WindowStyling.ApplyDarkFrame(this);
        Closed += (_, _) => { _closed = true; try { _cts.Cancel(); } catch { } };
        SetPerson(personName, address);
        Start();
    }

    /// <summary>Actualiza con quién vas a hablar (cuando la ventana ya está abierta y cambia el destinatario).</summary>
    public void SetPerson(string? personName, string? address)
    {
        _address = address;
        PersonText.Text = personName != null
            ? (string.IsNullOrWhiteSpace(address) ? $"Vas a ayudar a {personName}" : $"Vas a ayudar a {personName}  ·  {address}")
            : "Vas a dar soporte a alguien de la oficina.";
    }

    /// <summary>Prepara RustDesk y lo abre para conectar con la persona elegida (no hace nada si ya está en curso).</summary>
    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        RetryButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Preparando RustDesk… (la primera vez se descarga, puede tardar un momento)";
            var result = await _app.EnsureRustDeskAsync(_cts.Token);
            if (_closed) return;

            switch (result.Outcome)
            {
                case RustDeskOutcome.Ready when result.Path != null:
                    var opened = _app.LaunchRustDesk(result.Path, _address);
                    StatusText.Text = opened
                        ? (string.IsNullOrWhiteSpace(_address)
                            ? "RustDesk se está abriendo. Escribí la IP de la persona y pedile que acepte la conexión."
                            : $"RustDesk se está abriendo para conectar con {_address}. Pedile a la persona que acepte la conexión.")
                        : "No se pudo abrir RustDesk. Probá de nuevo.";
                    break;
                case RustDeskOutcome.NotPinned:
                    StatusText.Text = "RustDesk todavía no está habilitado en esta versión de Susurro: falta fijar la versión " +
                                      "verificada. Avisale a quien arma Susurro (ver RemoteSupport.PinnedRustDesk).";
                    break;
                default:
                    StatusText.Text = "No se pudo preparar RustDesk: sin conexión o la verificación de la descarga falló. Probá de nuevo.";
                    break;
            }
        }
        finally
        {
            _busy = false;
            if (!_closed) RetryButton.IsEnabled = true;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => Start();

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
