# Novedades

## Sin publicar

`feat/2-generador` · #1, #2, #3, #4: el catálogo de Otteretro pasa a ser un proyecto aparte.

### Nuevo
- **Administración** (#2): un programa con la misma interfaz de Otteretro para generar y revisar el catálogo, con sus claves. Tiene su propia carpeta y nunca abre los datos personales de Otteretro.
- **Catálogo publicado** (#3): un archivo JSON por saga, con ids permanentes. `catalogo publicar`, `catalogo validar` y `catalogo compilar` lo escriben, lo revisan y generan la base que baja la app.
- **Publicación automática** (#3): cada cambio de datos en `main` publica una versión.
- **Rutas, comparativas y sagas icónicas** (#7): se revisaron las 621 propuestas pendientes de Claude. Entraron 604: 36 sagas con rutas, 40 evaluadas como icónicas, 499 comparativas de versiones y 29 clasificaciones corregidas. Se rechazaron 17, por juegos sin lanzar, sin datos o con la ficha mezclada.
- **Imágenes compartidas con Otteretro** (#9): en Ajustes › General eliges la carpeta de imágenes, y ahí aparecen los discos donde está Otteretro. Con la carpeta de Otteretro, lo que baja un programa le sirve al otro, y fusionar o depurar juegos no borra nada de ella.
- **Aportes** (#4): una guía para proponer cambios, con la validación corriendo sola en cada PR.
