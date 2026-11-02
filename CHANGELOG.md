# Novedades

## Sin publicar

`feat/6-referencias-imagenes`, `feat/12-actualizaciones`, `feat/17-editar-sagas-rutas`, `feat/22-publicar-pr`, `feat/9-imagenes-compartidas` y `fix/33-sin-clave-steam` · #6, #9, #12, #17 y #22: imágenes por referencia y en el disco ★ de Otteretro, instalador, edición a mano y publicar con un PR.

### Nuevo
- **Referencias de imágenes** (#6): cada juego publica de dónde se baja su portada, su fondo y su logo (SteamGridDB, o la carátula de IGDB si no hay portada), sin copias ni claves. Otteretro y la administración bajan cada imagen la primera vez que se ve. `catalogo validar` rechaza una dirección que no sea de SteamGridDB o IGDB y cuenta cuántos juegos tienen cada tipo.
- **Instalador y actualizaciones** (#12): la administración se instala con `OtteretroCatalogo-win-Setup.exe` y se actualiza sola, con los mismos canales que Otteretro (Estable, Pruebas, Local y Otro origen) en Ajustes › General. Como en Otteretro, «Local» no pide carpeta: busca los paquetes en `<disco>\Otteretro\Paquetes\OtteretroCatalogo`, donde los deja `scripts/empaquetar.ps1`. Cada PR del programa publica una versión de Pruebas y cada fusión en `main`, una estable.
- **Editar sagas y rutas** (#17): en una saga, «Editar» permite cambiar su nombre y si es icónica, crear, ordenar o borrar sus rutas, y agregar, mover, fusionar o eliminar juegos; en Sagas, «Nueva saga». Lo fusionado o eliminado queda en `reemplazados.json`, y ni IGDB ni Claude pisan lo editado a mano.
- **Publicar con un PR** (#22): «Publicar» en una saga o un juego muestra qué cambia respecto de lo publicado y abre un PR, o actualiza el que ya está abierto. Con permiso de escritura usa una rama del repo; sin él, un fork en tu cuenta. Lo inválido no se publica, y si alguien cambió lo mismo en GitHub, avisa y no lo pisa. Necesita un token de GitHub con `public_repo` en Ajustes › Cuentas y claves; el programa nunca fusiona.
- **Imágenes en el disco ★ de Otteretro** (#9): no hay carpeta de imágenes propia. Van en `catalogo\imagenes` del disco ★ de Otteretro y lo siguen cuando Otteretro lo cambia, así lo que baja un programa le sirve al otro. Sin Otteretro, el asistente y Ajustes › General preparan el ★ como lo haría Otteretro, y cambiarlo lleva las imágenes. Fusionar o depurar juegos no borra imágenes.

### Cambios
- «Buscar imágenes en SteamGridDB e IGDB» guarda las direcciones en vez de bajar las imágenes, y vuelve a consultarlas para traer las que cambiaron.

### Arreglos
- **Sin aviso de Steam** (#33): Inicio ya no pide la clave de Steam Web API, que el catálogo no usa: Steam es de Otteretro.

## feat/2-generador · fusionado

`feat/2-generador` · #1, #2, #3, #4: el catálogo de Otteretro pasa a ser un proyecto aparte.

### Nuevo
- **Administración** (#2): un programa con la misma interfaz de Otteretro para generar y revisar el catálogo, con sus claves. Tiene su propia carpeta y nunca abre los datos personales de Otteretro.
- **Catálogo publicado** (#3): un archivo JSON por saga, con ids permanentes. `catalogo publicar`, `catalogo validar` y `catalogo compilar` lo escriben, lo revisan y generan la base que baja la app.
- **Publicación automática** (#3): cada cambio de datos en `main` publica una versión.
- **Rutas, comparativas y sagas icónicas** (#7): se revisaron las 621 propuestas pendientes de Claude. Entraron 604: 36 sagas con rutas, 40 evaluadas como icónicas, 499 comparativas de versiones y 29 clasificaciones corregidas. Se rechazaron 17, por juegos sin lanzar, sin datos o con la ficha mezclada.
- **Aportes** (#4): una guía para proponer cambios, con la validación corriendo sola en cada PR.
