# Novedades

## Sin publicar

`feat/2-generador` · #1, #2, #3, #4: el catálogo de Otteretro pasa a ser un proyecto aparte.

### Nuevo
- **Administración** (#2): un programa con la misma interfaz de Otteretro para generar y revisar el catálogo, con sus claves. Tiene su propia carpeta y nunca abre los datos personales de Otteretro.
- **Catálogo publicado** (#3): un archivo JSON por saga, con ids permanentes. `catalogo publicar`, `catalogo validar` y `catalogo compilar` lo escriben, lo revisan y generan la base que baja la app.
- **Publicación automática** (#3): cada cambio de datos en `main` publica una versión.
- **Aportes** (#4): una guía para proponer cambios, con la validación corriendo sola en cada PR.
