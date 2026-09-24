<#
.SYNOPSIS
  Crea (o quita) la regla de entrada del Firewall de Windows para Susurro.
  Requiere PowerShell como administrador. El instalador ya lo hace; esto es para instalaciones manuales.

.EXAMPLE
  .\scripts\firewall.ps1 -Exe "C:\Program Files\Susurro\Susurro.exe"
  .\scripts\firewall.ps1 -Remove
#>
param([string]$Exe = "$env:ProgramFiles\Susurro\Susurro.exe", [switch]$Remove, [switch]$AllProfiles)
$ErrorActionPreference = "Stop"
netsh advfirewall firewall delete rule name="Susurro" | Out-Null
if ($Remove) { Write-Host "Regla eliminada."; return }
if (-not (Test-Path $Exe)) { throw "No existe $Exe" }
$profile = if ($AllProfiles) { "any" } else { "private,domain" }
netsh advfirewall firewall add rule name="Susurro" dir=in action=allow program="$Exe" enable=yes profile=$profile
Write-Host "Regla creada para $Exe (perfiles: $profile)."
