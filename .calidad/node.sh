# ia-qa, perfil node: scripts del package.json (lint, typecheck, test), tsc si hay tsconfig, dependency-cruiser si hay config.
node_pm() { if [ -f pnpm-lock.yaml ]; then echo pnpm; elif [ -f yarn.lock ]; then echo yarn; else echo npm; fi; }
node_script() { grep -q "\"$1\" *:" package.json; }

node_instalar() {
  [ -d node_modules ] && return 0
  case $(node_pm) in
    pnpm) npx -y pnpm install --frozen-lockfile ;;
    yarn) npx -y yarn install --frozen-lockfile ;;
    *) if [ -f package-lock.json ]; then npm ci; else npm install; fi ;;
  esac
}

node_rapidos_web() {
  node_instalar
  if node_script lint; then echo "== lint"; npm run -s lint; fi
  if node_script typecheck; then echo "== typecheck"; npm run -s typecheck
  elif node_script check; then echo "== check"; npm run -s check
  elif [ -f tsconfig.json ]; then echo "== tsc"; npx tsc --noEmit; fi
}

node_ci_web() {
  node_rapidos_web
  if node_script test; then echo "== test"; npm test --silent; fi
  if ls .dependency-cruiser.* >/dev/null 2>&1; then echo "== dependency-cruiser"; npx -y dependency-cruiser@16 --validate -- src; fi
}

# shortcut: el front vive en web/ (no en la raíz); el perfil corre allí y además el `revisar` del repo (tipos y catálogos i18n).
node_rapidos() { (cd web && node_rapidos_web && npm run -s revisar); }
node_ci() { (cd web && node_ci_web); }
