<#
.SYNOPSIS
  Simula las dos PCs en una sola máquina para desarrollo.

  Instancia A → perfil "A", puerto TCP 47820
  Instancia B → perfil "B", puerto TCP 47830
  Ambas comparten el puerto UDP de descubrimiento (47811), así que se encuentran solas.
  Cada perfil tiene su propia configuración, identidad, contactos y registro en
  %LOCALAPPDATA%\Susurro\profiles\<perfil>\.

  La primera vez cada una pregunta un nombre (por ejemplo "Ana" y "Beto"); al elegirlo
  se encuentran solas. Si no, en Configuración → Personas → Agregar por dirección: 127.0.0.1:47820.

.EXAMPLE
  .\scripts\run-two-instances.ps1           # compila en Debug y abre A y B
  .\scripts\run-two-instances.ps1 -Reset    # borra los perfiles A y B (empezar de cero)
#>
param([switch]$Reset, [switch]$NoBuild)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if ($Reset) {
    foreach ($p in "A", "B") {
        $dir = Join-Path $env:LOCALAPPDATA "Susurro\profiles\$p"
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir; Write-Host "Perfil $p borrado" }
    }
}

if (-not $NoBuild) {
    dotnet build (Join-Path $root "src\Susurro.App") -c Debug -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }
}

$exe = Join-Path $root "src\Susurro.App\bin\Debug\net8.0-windows\Susurro.exe"
Start-Process $exe -ArgumentList "--profile A --port 47820"
Start-Process $exe -ArgumentList "--profile B --port 47830"
Write-Host "Instancias A (47820) y B (47830) iniciadas."
