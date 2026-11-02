# Empaqueta la administración del catálogo con Velopack en <OutputDir>\paquetes (#12). Lo usan las releases y las pruebas locales.
# Uso: .\scripts\empaquetar.ps1 [-Version 0.4.1] [-Channel win|dev] [-OutputDir <carpeta>]
# Sin -Version arma 0.FASE.N-local.M: una versión propia que no se confunde con las publicadas. Para instalarla, en
# Ajustes › General › Actualizaciones se elige «Local» con la carpeta <OutputDir>\paquetes.
param(
    [string]$Version,
    [string]$Channel = 'win',
    [string]$OutputDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artefactos')
)
$ErrorActionPreference = 'Stop'

$raiz = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = "$(& (Join-Path $PSScriptRoot 'version.ps1'))-local.$(git -C $raiz rev-list --count HEAD)" }
$publicado = Join-Path $OutputDir 'publicado'
$paquetes = Join-Path $OutputDir 'paquetes'

Remove-Item $publicado -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $raiz 'src\Otteretro') -c Release -r win-x64 --self-contained -p:Version=$Version -o $publicado
if ($LASTEXITCODE) { throw 'dotnet publish falló' }

# Su propio packId: se instala aparte de Otteretro y conviven (#12).
vpk pack --packId OtteretroCatalogo --packVersion $Version --packTitle 'Otteretro Catálogo' --packDir $publicado `
    --channel $Channel --mainExe OtteretroCatalogo.exe --icon (Join-Path $raiz 'src\Otteretro\nutria.ico') --outputDir $paquetes
if ($LASTEXITCODE) { throw 'vpk pack falló' }

Write-Host "Listo: $paquetes (instalador: OtteretroCatalogo-win-Setup.exe)"
