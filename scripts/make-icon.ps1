<#
  Genera src/Susurro.App/Assets/Susurro.ico (varios tamaños, PNG dentro de ICO).
  Diseño: cuadrado redondeado oscuro con dos "líneas de subtítulo".
  Solo hace falta volver a ejecutarlo si se cambia el diseño del icono.
#>
param([string]$Out = (Join-Path $PSScriptRoot "..\src\Susurro.App\Assets\Susurro.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.04)
    $bg = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 29, 33))), $bg)
    if ($s -ge 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 58, 61, 68)), ([Math]::Max(1, $s / 48))
        $g.DrawPath($pen, $bg)
    }

    $barH = [Math]::Max(2, [Math]::Round($s * 0.11))
    $gap = [Math]::Max(1, [Math]::Round($s * 0.07))
    $y2 = $s * 0.70
    $y1 = $y2 - $barH - $gap
    $w1 = $s * 0.62
    $w2 = $s * 0.40
    $r1 = New-RoundRect (($s - $w1) / 2) $y1 $w1 $barH ($barH / 2)
    $r2 = New-RoundRect (($s - $w2) / 2) $y2 $w2 $barH ($barH / 2)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 231, 232, 234))), $r1)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 138, 169, 214))), $r2)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Icono generado: $Out"
