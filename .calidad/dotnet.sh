# ia-qa, perfil dotnet: dotnet format (verify), build y test. Se detecta por *.sln / *.slnx / *.csproj.
# Con varias soluciones en la raíz, `dotnet` pide una: usa la primera (shortcut: monorepos con varias, fijar DOTNET_SLN en .calidad/local.sh).
# Sin solución no se pasa argumento: `dotnet` toma el .csproj de la carpeta (un "" lo hace fallar).
dotnet_objetivo() { echo "${DOTNET_SLN:-$(ls ./*.sln ./*.slnx 2>/dev/null | head -n1)}"; }
dotnet_cmd() { c=$1; shift; o=$(dotnet_objetivo); if [ -n "$o" ]; then dotnet "$c" "$o" "$@"; else dotnet "$c" "$@"; fi; }

dotnet_rapidos() {
  echo "== dotnet format"
  dotnet_cmd format --verify-no-changes
}

dotnet_ci() {
  dotnet_rapidos
  echo "== dotnet build"
  dotnet_cmd build -warnaserror --nologo -v q
  # shortcut: las pruebas son de Windows (rutas, Photino) y fallan en ubuntu; las corre validar.yml en windows-latest. Subir de nivel si calidad.yml pasa a correr en Windows.
}
