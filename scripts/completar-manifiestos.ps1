# #80: suma `schema` (lista ordenada de "tabla.columna" del catalogo.db, sin sqlite_* ni catalog_migration; igual que `catalogo compilar`) al manifiesto de cada release catalogo-* que no lo trae.
# Idempotente: lo que ya tiene `schema` no se toca. El sha256 y los demás campos quedan como estaban.
# Uso: .\scripts\completar-manifiestos.ps1 [-DryRun] [-Repo owner/repo]   (necesita gh y python)
#      .\scripts\completar-manifiestos.ps1 -Archivo catalogo.db.gz         (prueba local: solo imprime el schema)
param([switch]$DryRun, [string]$Repo = 'OscarVargas97/otteretro-catalogo', [string]$Archivo)
$ErrorActionPreference = 'Stop'

function Get-Schema([string]$gz) {
    $db = "$gz.plano"
    try {
        $in = [IO.File]::OpenRead($gz); $zip = [IO.Compression.GZipStream]::new($in, [IO.Compression.CompressionMode]::Decompress); $out = [IO.File]::Create($db)
        try { $zip.CopyTo($out) } finally { $out.Dispose(); $zip.Dispose(); $in.Dispose() }
        $py = "$gz.py"
        Set-Content $py -Encoding ASCII -Value @'
import sqlite3, sys, json
c = sqlite3.connect(sys.argv[1])
q = "SELECT m.name, p.name FROM sqlite_master m, pragma_table_info(m.name) p WHERE m.type='table' AND substr(m.name,1,7)<>'sqlite_' AND m.name<>'catalog_migration'"
print(json.dumps(sorted(t + '.' + col for t, col in c.execute(q))))
'@
        $json = python -I $py $db
        if ($LASTEXITCODE) { throw "no se pudo leer el esquema de $gz" }
        return @($json | ConvertFrom-Json)
    } finally { Remove-Item $db, "$gz.py" -ErrorAction SilentlyContinue }
}

if ($Archivo) { (Get-Schema (Resolve-Path $Archivo).Path) | ConvertTo-Json -Compress; return }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("manifiestos-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    $tags = (gh release list -R $Repo --limit 200 --json tagName | ConvertFrom-Json) | Where-Object { $_.tagName -like 'catalogo-*' } | ForEach-Object tagName
    foreach ($tag in $tags) {
        $dir = Join-Path $tmp $tag
        gh release download $tag -R $Repo -p catalogo.db.gz.json -D $dir
        $file = Join-Path $dir 'catalogo.db.gz.json'
        $manifest = Get-Content $file -Raw | ConvertFrom-Json
        if ($manifest.PSObject.Properties.Name -contains 'schema') { Write-Host "$tag : ya tiene schema"; continue }
        if ($DryRun) { Write-Host "$tag : le falta schema (en seco: no se baja ni se sube nada)"; continue }
        gh release download $tag -R $Repo -p catalogo.db.gz -D $dir
        $gz = Join-Path $dir 'catalogo.db.gz'
        if ((Get-FileHash $gz -Algorithm SHA256).Hash.ToLower() -ne $manifest.sha256) { Write-Warning "$tag : el sha256 de la base no coincide con el manifiesto; se omite"; continue }
        $manifest | Add-Member -NotePropertyName schema -NotePropertyValue (Get-Schema $gz)
        # UTF-8 sin BOM, como lo deja `catalogo compilar`.
        [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json -Compress -Depth 5), [Text.UTF8Encoding]::new($false))
        gh release upload $tag $file -R $Repo --clobber
        Write-Host "$tag : schema agregado ($($manifest.schema.Count) columnas)"
    }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
