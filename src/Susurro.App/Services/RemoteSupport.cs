using System;
using System.Diagnostics;
using Susurro.Core.Logging;
using Susurro.Core.RemoteSupport;

namespace Susurro.App.Services;

/// <summary>
/// Soporte remoto apoyado en RustDesk (open-source). Susurro solo <b>deja listo</b> RustDesk y lo <b>abre</b>
/// para conectar con la persona elegida —igual que antes abría Quick Assist—: la pantalla y el control los
/// maneja RustDesk, que pide permiso del lado de la otra persona en cada sesión. Susurro no captura la
/// pantalla de nadie ni le inyecta teclas o clics. No agregar acá captura ni transmisión de pantalla ajena
/// ni inyección de entrada por red.
/// <para>
/// RustDesk no viene adentro de Susurro ni se reparte por la actualización automática: se descarga <b>a
/// pedido</b> la primera vez que se usa <c>/control</c>, de una versión fijada y verificada
/// (ver <see cref="RustDeskProvisioner"/> en Core). Solo lo baja la PC que da el soporte y cuando la persona lo pide.
/// </para>
/// </summary>
internal static class RemoteSupport
{
    /// <summary>
    /// Versión de RustDesk que se fija. Fijar una versión auditada (en vez de «la última») es a propósito: así
    /// una release nueva de RustDesk —o una comprometida— no llega sola a las PCs de la oficina.
    /// <para>
    /// Para habilitar la descarga hay que completar <see cref="RustDeskRelease.Sha256"/> y
    /// <see cref="RustDeskRelease.Size"/> con los del binario que se auditó (desde una fuente confiable):
    /// </para>
    /// <code>
    /// PowerShell:  (Get-FileHash .\rustdesk-1.5.0-x86_64.exe -Algorithm SHA256).Hash
    ///              (Get-Item    .\rustdesk-1.5.0-x86_64.exe).Length
    /// </code>
    /// Mientras estén vacíos, <c>/control</c> avisa que falta configurarlo y <b>no descarga nada</b> (a prueba de fallas).
    /// </summary>
    public static readonly RustDeskRelease PinnedRustDesk = new()
    {
        Version = "1.5.0",
        Url = "https://github.com/rustdesk/rustdesk/releases/download/1.5.0/rustdesk-1.5.0-x86_64.exe",
        Sha256 = "", // ← completar: 64 dígitos hex del SHA-256 de la versión auditada
        Size = 0,    // ← completar: tamaño exacto del .exe en bytes
    };

    /// <summary>
    /// Abre RustDesk para conectar con <paramref name="address"/> (la IP de la persona en la LAN). Si no hay
    /// dirección, abre RustDesk a secas para que escribas vos la IP. No controla nada por su cuenta.
    /// </summary>
    public static bool LaunchRustDesk(string exePath, string? address)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(address))
            {
                // Acceso directo por IP en la LAN (sin servidor ni nube), como el resto de Susurro.
                psi.ArgumentList.Add("--connect");
                psi.ArgumentList.Add(address.Trim());
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("support", "No se pudo abrir RustDesk", ex);
            return false;
        }
    }
}
