using System.Text;

namespace Susurro.Core.Messaging;

/// <summary>Validación y limpieza del texto de los mensajes (idéntica al enviar y al recibir).</summary>
public static class MessageRules
{
    public const int MaxLength = 300;

    /// <summary>
    /// Convierte saltos de línea y tabulaciones en espacios, elimina caracteres de control
    /// y espacios repetidos, y recorta los extremos.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var ch = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (char.IsControl(ch)) continue;
            // Marcas de dirección bidi que podrían "voltear" el texto mostrado.
            if (ch is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩') continue;
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && lastWasSpace) continue;
            sb.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }
        return sb.ToString().Trim();
    }

    public static bool TryValidate(string? raw, out string text, out string? error)
    {
        text = Sanitize(raw);
        if (text.Length == 0)
        {
            error = "El mensaje está vacío.";
            return false;
        }
        if (text.Length > MaxLength)
        {
            error = $"Máximo {MaxLength} caracteres.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool IsValidMessageId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public static string NewMessageId() => Guid.NewGuid().ToString("N");
}
