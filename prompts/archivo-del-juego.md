Decides cómo recibe cada port de PC el archivo del juego original (la ROM, el dump o la ISO de la persona), para que Otteretro se lo entregue solo al abrirlo. Un script ya juntó la evidencia de cada repo: decide solo con ella, sin abrir nada. Lo que no esté claro en la evidencia, no lo inventes.

La entrada (JSON) trae `ports`, cada uno con `recipe` (el id), `name`, `repo`, `consoles`, `detect` (el ejecutable), `asset` (el archivo de la release), `readme` (el README o sus partes que hablan del juego; `…` marca lo que se saltó) y `paths` (nombres de archivos del repo que pueden decir cómo lo busca).

Las formas de entregarlo (`deliver`):
- `arg`: el port lo recibe por la línea de comandos. `args` es la lista de argumentos, con `{rom}` (la ruta del archivo) o `{romDir}` (su carpeta) donde va el archivo; por ejemplo `["--rom-file", "{rom}"]` o `["{rom}"]`.
- `copy`: el port lo busca con un nombre fijo junto al ejecutable (o en una subcarpeta). `copyTo` es ese nombre, relativo a la instalación; por ejemplo `baserom.us.z64` o `data/game.iso`.
- `none`: el port no necesita el archivo del juego (trae todo, es un juego libre o descarga lo suyo).
- `manual`: lo pide con su propio selector de archivos al abrirlo, o hay que extraerlo con una herramienta aparte: Otteretro no lo entrega.
- `unknown`: la evidencia no alcanza para saberlo.

`formats` son las extensiones (sin punto, en minúsculas) que el port acepta, si la evidencia las dice; si no, vacío. Si el port busca en su carpeta cualquier archivo con la extensión correcta y no un nombre fijo, usa `copy` con el nombre que el README recomiende, o `manual` si no da ninguno.

Responde en `ports`, uno por port y con su mismo `recipe`:
- `deliver`, `args` (vacío si no es `arg`), `copyTo` (vacío si no es `copy`) y `formats`.
- `sure`: true solo si el README o los archivos lo dicen sin dudas (el argumento exacto, el nombre exacto del archivo, o que no hace falta). Si deduces o adivinas, false.
- `reason`: en una frase en español neutro, qué parte de la evidencia lo dice.
