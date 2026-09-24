<#
.SYNOPSIS
  Compila, prueba, publica y (si Inno Setup está instalado) genera SusurroSetup.exe.

.EXAMPLE
  .\scripts\build.ps1                 # tests + publicación (Susurro.exe único) + instalador
  .\scripts\build.ps1 -SkipTests
  .\scripts\build.ps1 -Version 1.0.1
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

if (-not $SkipTests) {
    Write-Host "== Tests" -ForegroundColor Cyan
    dotnet test tests/Susurro.Core.Tests -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "Los tests fallaron" }
}

Write-Host "== Publicación autocontenida win-x64" -ForegroundColor Cyan
$publish = Join-Path $root "artifacts\publish\win-x64"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish src/Susurro.App -c Release -p:PublishProfile=win-x64 -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw "La publicación falló" }

Write-Host "== Ejecutable único" -ForegroundColor Cyan
$release = Join-Path $root "artifacts\release"
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item (Join-Path $publish "Susurro.exe") (Join-Path $release "Susurro.exe") -Force
Write-Host "artifacts\release\Susurro.exe"

Write-Host "== Instalador" -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" (Join-Path $root "installer\Susurro.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló" }
    Write-Host "Instalador: artifacts\installer\SusurroSetup.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup 6 no está instalado: se omite SusurroSetup.exe." -ForegroundColor Yellow
    Write-Host "Instalalo desde https://jrsoftware.org/isdl.php (o 'winget install JRSoftware.InnoSetup') y volvé a ejecutar este script."
    Write-Host "Mientras tanto podés distribuir artifacts\release\Susurro.exe directamente."
}
Write-Host "Listo." -ForegroundColor Green
