# La versión 0.FASE.N (#12, como en Otteretro): VERSION dice la fase y N son los PRs fusionados en main desde que se abrió.
# Uso: .\scripts\version.ps1 [-Rev HEAD]
param([string]$Rev = 'HEAD')
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
function Fase([string]$commit) { (git -C $raiz show "${commit}:VERSION").Trim() -replace '^(\d+\.\d+).*', '$1' }

$fase = Fase $Rev
# El commit que abrió la fase: el más viejo de los últimos cambios de VERSION que ya dicen esta fase.
$base = $null
foreach ($c in git -C $raiz log --format=%H $Rev -- VERSION) {
    if ((Fase $c) -ne $fase) { break }
    $base = $c
}
$n = [int](git -C $raiz rev-list --count --merges --first-parent "$base..$Rev")
"$fase.$n"
