# ia-qa: checks comunes a todos los stacks. Lo carga .calidad/checks.sh.
umbral() { v=$(sed -n "s/^ *$1: *\([0-9][0-9]*\).*/\1/p" .calidad.yml 2>/dev/null | head -n1); echo "${v:-$2}"; }

# Corre una herramienta Python sin instalarla en el proyecto: uvx, pipx o la instalada.
py_tool() { t=$1; shift
  if command -v uvx >/dev/null 2>&1; then uvx -q "$t" "$@"
  elif command -v pipx >/dev/null 2>&1; then
    if [ "$t" = --from ]; then s=$1; shift; pipx run --quiet --spec "$s" "$@"; else pipx run --quiet "$t" "$@"; fi
  elif [ "$t" = --from ]; then shift; "$@"
  elif command -v "$t" >/dev/null 2>&1; then "$t" "$@"
  else python -m "$t" "$@"; fi; }

calidad_base() { echo "${CALIDAD_BASE:-$(git merge-base HEAD origin/HEAD 2>/dev/null || git merge-base HEAD origin/main)}"; }

archivos_cambiados() { git diff --name-only --diff-filter=AM "$(calidad_base)"...HEAD -- "$@"; }

# Funciones sobre el umbral como «archivo función», sin línea: si el código se mueve sigue siendo la misma.
lizard_excesos() {
  py_tool lizard -w -C "$(umbral complejidad_ciclomatica 10)" -L "$(umbral lineas_por_funcion 60)" \
    -a "$(umbral parametros_por_funcion 5)" "$@" 2>/dev/null \
    | sed -n 's/^\(.*\):[0-9]*: warning: \([^ ]*\) has.*/\1 \2/p' | sort -u
}

nucleo_rapidos() { :; }

nucleo_ci() {
  files=$(archivos_cambiados '*.py' '*.js' '*.jsx' '*.ts' '*.tsx' '*.go' '*.rs' '*.java' '*.cs' '*.rb' '*.php' '*.swift' '*.kt')
  if [ -n "$files" ]; then
    # Trinquete: falla solo por funciones sobre el umbral que no lo estaban en la base (la deuda vieja no bloquea).
    echo "== lizard (funciones nuevas sobre el umbral)"
    base=$(calidad_base); tmp=$(mktemp -d); antes=""
    for f in $files; do
      if git cat-file -e "$base:$f" 2>/dev/null; then
        mkdir -p "$tmp/$(dirname "$f")"; git show "$base:$f" > "$tmp/$f"; antes="$antes $f"
      fi
    done
    # shellcheck disable=SC2086
    if [ -n "$antes" ]; then (cd "$tmp" && lizard_excesos $antes) > "$tmp.antes"; else : > "$tmp.antes"; fi
    # shellcheck disable=SC2086
    lizard_excesos $files > "$tmp.ahora"
    nuevos=$(comm -13 "$tmp.antes" "$tmp.ahora"); rm -rf "$tmp" "$tmp.antes" "$tmp.ahora"
    if [ -n "$nuevos" ]; then
      printf '%s\n' "$nuevos" | sed 's/^/  /'
      echo "Funciones sobre el umbral (complejidad, largo o parámetros) en este cambio: divídelas." >&2; return 1
    fi
    echo "  ok"
  fi
  echo "== jscpd (duplicación del repo)"
  npx -y jscpd@4 --silent --threshold "$(umbral duplicacion_pct 3)" \
    --format "javascript,typescript,tsx,jsx,python,go,rust,java,csharp,ruby,php,swift,kotlin" \
    --ignore "**/node_modules/**,**/dist/**,**/build/**,**/.venv/**,**/legacy/**,**/*.min.js" .
}
