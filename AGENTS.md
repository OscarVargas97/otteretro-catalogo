# Otteretro Catálogo — instrucciones para agentes de IA

El catálogo de sagas y juegos de [Otteretro](https://github.com/OscarVargas97/otteretro): su generador, su administración y lo publicado. Es un programa aparte de la app: la persona edita y publica el catálogo aquí, y Otteretro baja lo publicado.

C# ASP.NET Core + SQLite, interfaz Next.js estática en una ventana Photino, con el mismo estilo que Otteretro. Nació de una copia de esa app, sin lo que no es catálogo.

## Qué vive aquí y qué no

- **Aquí:** IGDB, carga inicial, depurar sagas, rutas y comparar versiones con Claude, Revisar, Aprendizajes, corregir datos a mano, consolas, imágenes y sus claves.
- **En Otteretro:** colección, progreso, enlaces, identificar archivos de la persona, Steam y descargas.
- **Nunca aquí:** datos de una persona. Este programa tiene su propia carpeta y su propia configuración (`%LocalAppData%\OtteretroCatalogo`), y no abre la base personal de Otteretro.

## Cómo se trabaja

Con Claude Code en un worktree por rama (más adelante, desde [ottercode](https://github.com/OscarVargas97/ottercode), proyecto `game-system`): un issue por tarea y una rama por issue (`<tipo>/<n>-<slug>`); el estado en el issue y sin issue no hay trabajo. Las decisiones se registran en `docs/historial/decisiones.md` de Otteretro, el registro único del proyecto. Commits en Conventional Commits, en español, con `Refs #n`. Todo entra a `main` por PR, y lo fusiona la persona. Los checks de calidad (ia-qa) viven en `.calidad/`: `sh .calidad/checks.sh rapidos` antes de subir y `ci` como en el PR; los hooks de git se activan con `git config core.hooksPath .githooks`.

## Reglas

- Claude se usa a través del Claude Code del usuario (`claude -p`), **nunca con la API de Anthropic**.
- Identificadores en inglés; interfaz, documentación, comentarios, commits e issues en español neutro.
- Interfaz en español e inglés: los textos viven en `web/messages/<idioma>.json`, y cada texto nuevo va en los dos catálogos.
- El repo es público: credenciales y datos de personas nunca en el repo ni en logs.
- Lo publicado atribuye sus datos a [IGDB.com](https://www.igdb.com).

## Comandos

| Para | Comando |
|---|---|
| Pruebas | `dotnet test` |
| Compilar todo, con el front | `dotnet build src/Otteretro -c Release` |
| Revisar tipos y catálogos del front | `npm --prefix web run revisar` |
| Pruebas de interfaz | `npm --prefix web run e2e` |
| Regenerar los tipos del front tras cambiar la API | `cd web && npm run tipos` |
| Programa listo para abrir | `./scripts/publicar.ps1 -OutputDir <carpeta>` |
| De qué commit de `main` parte el catálogo local (para avisar de conflictos al publicar) | `OtteretroCatalogo catalogo base <sha>` |
| Instalador y paquetes (canal Local) | `./scripts/empaquetar.ps1 [-Disk D:]`: los deja en `<disco>\Otteretro\Paquetes\OtteretroCatalogo` (necesita `vpk`: `dotnet tool install -g vpk`) |
| Traer una copia de un `catalogo.db` a un catálogo vacío | `OtteretroCatalogo catalogo importar <base.db>` |
| Escribir el catálogo comprimido, sin correcciones ni reglas de Claude | `OtteretroCatalogo catalogo exportar <archivo>` |
| Probar sin tocar la config real | `OTTERETRO_CONFIG=<ruta>/config.json OtteretroCatalogo servidor` |
