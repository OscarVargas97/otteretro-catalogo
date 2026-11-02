# Novedades

## Sin publicar

`feat/2-generador` · #1, #2, #3, #4: el catálogo de Otteretro pasa a ser un proyecto aparte.

### Nuevo
- **Administración** (#2): un programa con la misma interfaz de Otteretro para generar y revisar el catálogo, con sus claves. Tiene su propia carpeta y nunca abre los datos personales de Otteretro.
- **Catálogo publicado** (#3): un archivo JSON por saga, con ids permanentes. `catalogo publicar`, `catalogo validar` y `catalogo compilar` lo escriben, lo revisan y generan la base que baja la app.
- **Publicación automática** (#3): cada cambio de datos en `main` publica una versión.
- **Rutas, comparativas y sagas icónicas** (#7): se revisaron las 621 propuestas pendientes de Claude. Entraron 604: 36 sagas con rutas, 40 evaluadas como icónicas, 499 comparativas de versiones y 29 clasificaciones corregidas. Se rechazaron 17, por juegos sin lanzar, sin datos o con la ficha mezclada.
- **Editar sagas y rutas** (#17): en una saga, «Editar» permite cambiar su nombre y si es icónica, crear, ordenar o borrar sus rutas, y agregar, mover, fusionar o eliminar juegos; en Sagas, «Nueva saga». Lo fusionado o eliminado queda en `reemplazados.json`, y ni IGDB ni Claude pisan lo editado a mano.
- **Publicar con un PR** (#22): «Publicar» en una saga o un juego muestra qué cambia respecto de lo publicado y abre un PR, o actualiza el que ya está abierto. Con permiso de escritura usa una rama del repo; sin él, un fork en tu cuenta. Lo inválido no se publica, y si alguien cambió lo mismo en GitHub, avisa y no lo pisa. Necesita un token de GitHub con `public_repo` en Ajustes › Cuentas y claves; el programa nunca fusiona.
- **Instalador y actualizaciones** (#12): la administración se instala con `OtteretroCatalogo-win-Setup.exe` y se actualiza sola, con los mismos canales que Otteretro (Estable, Pruebas, Local y Otro origen) en Ajustes › General. Como en Otteretro, «Local» no pide carpeta: busca los paquetes en `<disco>\Otteretro\Paquetes\OtteretroCatalogo`, donde los deja `scripts/empaquetar.ps1`. Cada PR del programa publica una versión de Pruebas y cada fusión en `main`, una estable.
- **Aportes** (#4): una guía para proponer cambios, con la validación corriendo sola en cada PR.
