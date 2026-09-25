using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Susurro.App.Native;
using Susurro.Core.Logging;
using Susurro.Core.Transfers;

namespace Susurro.App.Services;

/// <summary>Algo adjunto al mensaje que se está escribiendo.</summary>
internal abstract record Attachment(string Label);

/// <summary>Imagen pegada (PNG o JPEG listo para enviar) con su miniatura.</summary>
internal sealed record ImageAttachment(byte[] Data, int PixelWidth, int PixelHeight, BitmapSource Thumbnail)
    : Attachment($"Imagen {PixelWidth}×{PixelHeight} · {FileNames.FormatSize(Data.Length)}");

internal sealed record FileAttachment(string Path, long Size)
    : Attachment($"{System.IO.Path.GetFileName(Path)} · {FileNames.FormatSize(Size)}");

internal static class Attachments
{
    /// <summary>
    /// Lee el portapapeles: archivos copiados en el Explorador o una imagen (captura, "Copiar imagen").
    /// Si hay texto, no se toma la imagen (así Ctrl+V de un texto con formato sigue pegando el texto).
    /// Devuelve null si no hay nada adjuntable.
    /// </summary>
    public static List<Attachment>? FromClipboard(out string? error)
    {
        error = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                return FromPaths(files, out error);
            }
            if (Clipboard.ContainsText()) return null;
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
            {
                var img = FromPng(png.ToArray(), out error);
                return img == null ? null : new List<Attachment> { img };
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bmp)
            {
                var img = FromBitmap(bmp, out error);
                return img == null ? null : new List<Attachment> { img };
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or NotSupportedException)
        {
            // El portapapeles está ocupado por otro programa o el formato no se puede leer.
            error = "No se pudo leer el portapapeles. Probá copiar de nuevo.";
            Log.Warn("files", "Portapapeles ilegible", ex);
        }
        return null;
    }

    /// <summary>Archivos elegidos, arrastrados o copiados (las carpetas se ignoran).</summary>
    public static List<Attachment> FromPaths(IEnumerable<string> paths, out string? error)
    {
        error = null;
        var list = new List<Attachment>();
        foreach (var p in paths)
        {
            try
            {
                var info = new FileInfo(p);
                if (!info.Exists)
                {
                    if (Directory.Exists(p)) error = "Las carpetas no se pueden enviar: comprimila en un .zip.";
                    continue;
                }
                if (info.Length > TransferLimits.MaxFileBytes)
                {
                    error = $"«{info.Name}» es muy grande (máximo {FileNames.FormatSize(TransferLimits.MaxFileBytes)}).";
                    continue;
                }
                list.Add(new FileAttachment(info.FullName, info.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = "No se pudo leer el archivo: " + ex.Message;
            }
        }
        return list;
    }

    private static ImageAttachment? FromPng(byte[] png, out string? error)
    {
        error = null;
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (png.Length <= TransferLimits.MaxImageBytes)
                return new ImageAttachment(png, frame.PixelWidth, frame.PixelHeight, Thumbnail(frame));
            return FromBitmap(frame, out error);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Codifica en PNG; si pasa de 10 MB, en JPEG; y si aún así no entra, la achica. La transparencia
    /// que Windows a veces agrega mal al copiar se descarta (las capturas no la usan).
    /// </summary>
    public static ImageAttachment? FromBitmap(BitmapSource source, out string? error)
    {
        error = null;
        BitmapSource bmp = source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var data = Encode(new PngBitmapEncoder(), bmp);
        var scale = 1.0;
        while (data.Length > TransferLimits.MaxImageBytes && scale > 0.2)
        {
            BitmapSource candidate = scale >= 1 ? bmp : new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            data = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, candidate);
            if (data.Length > TransferLimits.MaxImageBytes) scale *= 0.75;
            else bmp = candidate;
        }
        if (data.Length > TransferLimits.MaxImageBytes)
        {
            error = "La imagen es demasiado grande.";
            return null;
        }
        return new ImageAttachment(data, bmp.PixelWidth, bmp.PixelHeight, Thumbnail(bmp));
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource bmp)
    {
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource Thumbnail(BitmapSource bmp)
    {
        var s = Math.Min(1.0, 72.0 / Math.Max(1, bmp.PixelHeight));
        BitmapSource thumb = s < 1 ? new TransformedBitmap(bmp, new ScaleTransform(s, s)) : bmp;
        var copy = new WriteableBitmap(thumb); // desacopla la miniatura del original (que se libera)
        copy.Freeze();
        return copy;
    }

    /// <summary>Carpeta Descargas del usuario (la real, aunque esté movida a otro disco).</summary>
    public static string DownloadsFolder()
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(NativeMethods.FOLDERID_Downloads, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
        catch
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Extensión según los primeros bytes (PNG o JPEG).</summary>
    public static string ImageExtension(byte[] data) =>
        data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 ? ".png"
        : data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8 ? ".jpg"
        : ".img";
}
