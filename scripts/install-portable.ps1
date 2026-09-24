<#
.SYNOPSIS
  Instalación sin instalador (alternativa a SusurroSetup.exe). Ejecutar desde la carpeta
  donde están Susurro.exe y sus DLL (por ejemplo, el zip descomprimido).

  - Copia los archivos a %LOCALAPPDATA%\Programs\Susurro (sin permisos de administrador).
  - Crea accesos directos en el menú Inicio (y opcionalmente en el escritorio).
  - Opcional: inicio con Windows.
  - Opcional: regla de firewall (pide permisos de administrador).
  - Registra "Susurro" en Aplicaciones instaladas para desinstalarlo limpiamente.

.EXAMPLE
  .\install-portable.ps1 -AutoStart -Firewall
  .\install-portable.ps1 -Uninstall
#>
param([switch]$AutoStart, [switch]$Firewall, [switch]$Desktop, [switch]$Uninstall)
$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\Susurro"
$exe = Join-Path $target "Susurro.exe"
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Susurro.lnk"
$desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Susurro.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Susurro"

function Invoke-Elevated([string]$command) {
    Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList "-NoProfile", "-Command", $command
}

Get-Process Susurro -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    if (Test-Path $exe) { & $exe --uninstall-cleanup | Out-Null }
    Remove-Item $startMenu, $desktopLnk -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    Remove-Item $uninstallKey -ErrorAction SilentlyContinue
    try { Invoke-Elevated 'netsh advfirewall firewall delete rule name="Susurro"' } catch { }
    Write-Host "Susurro desinstalado. La configuración queda en $env:LOCALAPPDATA\Susurro (borrala a mano si querés)."
    return
}

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source "Susurro.exe"))) { throw "Ejecutá este script desde la carpeta que contiene Susurro.exe" }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu) + $(if ($Desktop) { @($desktopLnk) } else { @() })) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $target
    $s.IconLocation = "$exe,0"
    $s.Save()
}

& $exe --set-autostart $(if ($AutoStart) { "on" } else { "off" }) | Out-Null

if ($Firewall) {
    Invoke-Elevated "netsh advfirewall firewall delete rule name=`"Susurro`"; netsh advfirewall firewall add rule name=`"Susurro`" dir=in action=allow program=`"$exe`" enable=yes profile=private,domain"
}

New-Item -Force $uninstallKey | Out-Null
Set-ItemProperty $uninstallKey -Name DisplayName -Value "Susurro"
Set-ItemProperty $uninstallKey -Name DisplayIcon -Value $exe
Set-ItemProperty $uninstallKey -Name Publisher -Value "Susurro"
Set-ItemProperty $uninstallKey -Name InstallLocation -Value $target
Copy-Item $PSCommandPath (Join-Path $target "install-portable.ps1") -Force -ErrorAction SilentlyContinue
Set-ItemProperty $uninstallKey -Name UninstallString -Value "powershell -NoProfile -ExecutionPolicy Bypass -File `"$target\install-portable.ps1`" -Uninstall"
Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

Start-Process $exe
Write-Host "Susurro instalado en $target"
