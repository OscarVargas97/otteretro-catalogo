# Deja la administración del catálogo lista para abrir en <OutputDir>. Uso: .\scripts\publicar.ps1 -OutputDir <carpeta>
param([Parameter(Mandatory)][string]$OutputDir)
$ErrorActionPreference = 'Stop'

$raiz = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $raiz 'src\Otteretro') -c Release -r win-x64 --self-contained -o $OutputDir
if ($LASTEXITCODE) { throw 'dotnet publish falló' }
Write-Host "Listo: $(Join-Path $OutputDir 'OtteretroCatalogo.exe')"
