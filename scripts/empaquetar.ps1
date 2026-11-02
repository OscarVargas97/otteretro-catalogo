# Empaqueta la administración del catálogo con Velopack en <OutputDir>\paquetes (#12). Lo usan las releases y las pruebas locales.
# Uso: .\scripts\empaquetar.ps1 [-Version 0.4.1] [-Channel win|dev] [-OutputDir <carpeta>] [-Disk D:]
# Sin -Version arma 0.FASE.N-local.M: una versión propia que no se confunde con las publicadas. Va a
# <disco>\Otteretro\Paquetes\OtteretroCatalogo, como en Otteretro, y en Ajustes › General › Actualizaciones basta elegir «Local».
# Con varios discos de Otteretro se elige con -Disk. Con -Version o -OutputDir, como las releases, quedan en <OutputDir>\paquetes.
param(
    [string]$Version,
    [string]$Channel = 'win',
    [string]$OutputDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artefactos'),
    [string]$Disk
)
$ErrorActionPreference = 'Stop'

$raiz = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = "$(& (Join-Path $PSScriptRoot 'version.ps1'))-local.$(git -C $raiz rev-list --count HEAD)" }
$publicado = Join-Path $OutputDir 'publicado'
$paquetes = Join-Path $OutputDir 'paquetes'
if ($Disk -or -not ($PSBoundParameters.ContainsKey('Version') -or $PSBoundParameters.ContainsKey('OutputDir'))) {
    $discos = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'Otteretro' } | Where-Object { Test-Path (Join-Path $_ 'otteretro-ubicacion.json') })
    if ($Disk) { $discos = @($discos | Where-Object { $_ -like "$($Disk.TrimEnd('\'))*" }) }
    if ($discos.Count -ne 1) { throw "Hay $($discos.Count) discos de Otteretro: elige uno con -Disk D:" }
    $paquetes = Join-Path $discos[0] 'Paquetes\OtteretroCatalogo'
}

Remove-Item $publicado -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $raiz 'src\Otteretro') -c Release -r win-x64 --self-contained -p:Version=$Version -o $publicado
if ($LASTEXITCODE) { throw 'dotnet publish falló' }

# Su propio packId: se instala aparte de Otteretro y conviven (#12). La á va por su código: Windows PowerShell 5.1 lee
# este archivo como ANSI y el nombre del acceso directo saldría roto.
vpk pack --packId OtteretroCatalogo --packVersion $Version --packTitle "Otteretro Cat$([char]0xE1)logo" --packDir $publicado `
    --channel $Channel --mainExe OtteretroCatalogo.exe --icon (Join-Path $raiz 'src\Otteretro\nutria.ico') --outputDir $paquetes
if ($LASTEXITCODE) { throw 'vpk pack falló' }

Write-Host "Listo: $paquetes (instalador: OtteretroCatalogo-win-Setup.exe)"
