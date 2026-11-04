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
# Los PRs fusionados por su asunto, con commit de fusión («Merge pull request #n») o con squash («… (#n)», #259).
$n = @(git -C $raiz log --first-parent --format=%s "$base..$Rev" | Where-Object { $_ -match '^Merge pull request #\d+|\(#\d+\)$' }).Count
"$fase.$n"
