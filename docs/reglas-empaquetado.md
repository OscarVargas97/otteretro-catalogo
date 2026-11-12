# Reglas de empaquetado de las releases

Cómo viene empaquetado el archivo de Windows de una release y qué hace el catálogo con él para que la receta quede jugable: qué archivo se baja (`asset`), qué ejecutable se abre (`detect`), si hay que correr un instalador (`installer`) o renombrar lo bajado (`saveAs`). Vale igual para los ports y para los emuladores.

El código está en `src/Otteretro/Catalog/AssetFiles.cs` (`Pick`, `Packer`, `Resolve`) y se usa en tres lugares:

- **Recetas de ports** (`PortRecipes.Create`): la receta nueva sale con lo que digan estas reglas y, si algo no se sabe, con un aviso en su tarjeta.
- **«Crear desde su repo»** (`Recipes.Draft`): el formulario sale completo o con la lista de .exe para elegir.
- **Revisar emuladores** (`EmulatorCheck.CheckDetect`): cada release nueva se comprueba; lo que no calza va a Revisar.

Otteretro, al instalar, hace una última comprobación: si lo instalado no trae el `detect`, la instalación falla limpia (otteretro#440). Es la red para lo que el catálogo no puede saber sin bajar el archivo (un 7z, un instalador).

## Relevamiento (otteretro-catalogo#179, 711 recetas con repo)

| Caso | Recetas | Cómo se reconoce | Qué se hace |
|---|---|---|---|
| Zip con un .exe claro | 418 + 49 corregidas | El directorio central del zip, leído por HTTP Range sin bajarlo | `detect` = ese .exe (ver «Elegir el ejecutable») |
| Zip con varios .exe sin uno claro | 51 | Quedan dos o más candidatos | Aviso «Elegir el ejecutable» con la lista |
| Zip sin .exe | 5 | Solo .dll, .bat, .py o mapas | Aviso «no trae ningún .exe»: otro archivo o la receta se marca |
| Zip con un instalador adentro | 4 | La raíz solo trae `*Setup*.exe` / `Installer.exe` | Aviso «Elegir el ejecutable»; correr un instalador de adentro de un zip no está hecho |
| .exe suelto (el juego) | 20 | Un .exe sin firma de instalador ni «setup» en el nombre | `detect` = el mismo .exe |
| .exe suelto con la versión en el nombre | 7 | `\d+\.\d+` en el nombre | `saveAs` y `detect` = el nombre del repo + `.exe`, que no cambia entre versiones |
| Instalador Inno Setup | 14 | «Inno Setup» en los primeros 2 MB | `installer`: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR={dir}` |
| Instalador NSIS | 5 | «NullsoftInst» en los primeros 2 MB | Aviso «instalador sin argumentos». `/S /D={dir}` necesita otteretro#445 (`/D=` sin comillas) |
| Instalador propio | 27 | «setup» o «install» en el nombre, sin firma conocida (un stub con un zip pegado, como el de NX2navi) | Aviso «instalador sin argumentos»: hay que leer su código o su ayuda (`--help`) y escribirlos a mano |
| 7z, rar, tar.gz | 12 | La extensión | No se puede leer por Range: se confía en el `detect` y Otteretro lo comprueba al instalar |
| Sin patrón de archivo | 88 | La receta no tiene `asset` | Aviso «Elegir el archivo» (varios sin uno claro) |
| Patrón que no calza | 11 | Ningún archivo de la última release calza con `asset` | Aviso «ningún archivo calza», con el patrón propuesto |
| Repo que ya no existe | 3 | 404 | Aviso «el repo ya no existe» |
| Release sin archivos | 1 | | Queda sin receta de Windows |

Proyectado a 1000 ports con las mismas proporciones: unos 710 se resuelven solos, unos 20 llevan `installer` solos, y unos 270 dejan un aviso en su tarjeta (dos de cada tres por elegir el archivo o el ejecutable, que es un clic; el resto, instaladores sin argumentos conocidos y patrones viejos).

## Elegir el ejecutable (`Pick`)

1. **Ayudantes fuera.** No es el juego un .exe cuyo nombre trae: `vc_redist`, `unins`, `setup`, `install`, `update`, `crash`, `report`, `serve` (servidor), `relay`, `headless`, `settings`, `shadereditor`, `extract`, `probe`, `capabilit`, `check`, `cache`, `ded` como palabra (servidor dedicado), o termina en `-room`, `-cli`, `-dev`.
2. **Dónde se busca.** En la raíz de lo instalado (si todo viene en una carpeta, desde ella, como Otteretro al extraer). Si la raíz no trae ningún .exe, en las subcarpetas (`bin/juego.exe`). Si la raíz trae solo un instalador, lo de las subcarpetas es suyo y no se elige.
3. **El único** de la raíz.
4. **El que se llama como el repo**, sin contar guiones, espacios ni mayúsculas (`Kameo-RePowered` → `KameoRePowered.exe`).
5. **Juego y lanzador** («Play X.exe», «X Launcher.exe», «x-launcher.exe»): el que no es lanzador, porque el lanzador abre ese mismo juego. **No en un emulador**: ahí el lanzador es su interfaz (kyty).
6. Si no queda uno: **lo elige la persona** (variantes avx2/sse4, regiones pal/jpn, x64/ARM64, sdl2/vulkan, juego + herramientas con nombres propios).

## Un .exe como archivo (`Packer`)

Se leen los primeros 2 MB. Por firma: `inno`, `nsis`, `7zsfx`; sin firma pero con «setup» o «install» en el nombre: `setup`. Si no es ninguno, es el juego.

- **Inno**: se conocen sus argumentos y la receta los lleva. Lo que instala no se sabe sin correrlo: el `detect` queda en el supuesto (el nombre del repo) con el aviso «revisar el ejecutable que deja».
- **NSIS**: `/S /D={dir}` cuando esté otteretro#445.
- **7z SFX**: se puede extraer como un 7z; no está hecho (ninguna receta lo usa hoy).
- **Propio** (`setup`): sin argumentos comunes. Ejemplo: NX2navi acepta `--dest {dir} --no-shortcuts --no-launch --quiet` (otteretro-catalogo#176).

## Casos nuevos

Lo que no calza con nada de arriba deja un aviso en la tarjeta de la receta: con el aviso a la vista, la regla nueva se suma aquí, en `AssetFiles` y en `AssetFilesTests` (una línea de `InlineData` por caso).

## Riesgos conocidos

- Un instalador Inno registra un desinstalador en Windows que apunta a la carpeta `.parcial` (Otteretro la renombra después) y puede pedir permisos de administrador si no admite instalar por usuario.
- Un ayudante con un nombre que no está en la lista se toma por el juego si es el único de la raíz.
- La regla del lanzador supone que el lanzador abre el mismo juego; si el juego necesita el lanzador para configurarse, hay que cambiar el `detect` a mano.
