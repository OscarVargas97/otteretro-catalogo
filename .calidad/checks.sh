#!/bin/sh
# ia-qa: checks del repo. Modos: rapidos (pre-push y hook Stop) | ci (cada PR).
# Lo genera la skill `calidad`; lo particular del repo va en .calidad/local.sh (funciones local_rapidos/local_ci).
set -e
modo=${1:-rapidos}
d=$(dirname "$0")
# modo: minimo (repo sin stack): sin perfiles ni núcleo (lint, tests, complejidad); gitleaks y los checks del flujo los corren pre-push y el CI.
if grep -Eq '^modo: *"?m[ií]nimo' .calidad.yml 2>/dev/null; then :; else
. "$d/nucleo.sh"
stacks=$(sed -n 's/^stacks: *\[\(.*\)\].*/\1/p' .calidad.yml 2>/dev/null | tr ',' ' ')
for s in $stacks; do . "$d/$s.sh"; "${s}_$modo"; done
nucleo_$modo
fi
if [ -f "$d/local.sh" ]; then . "$d/local.sh"; ! command -v "local_$modo" >/dev/null || "local_$modo"; fi
echo "checks $modo: OK"
