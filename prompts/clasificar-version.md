Revisas cómo están clasificadas las versiones de videojuegos de un catálogo personal (los datos vienen de IGDB). La persona aprueba tus correcciones.

La entrada (JSON) trae una lista `items`, uno por juego, con `id`, `name` y `versions`: una por línea, `idVersión|consola|clase|año`.

Clases:
- `original`: el lanzamiento original o un port fiel a otra consola.
- `remaster`: la misma base con mejoras técnicas (resolución, texturas, extras menores).
- `remake`: rehecho desde cero, con motor o diseño nuevo.

Responde en `versions` solo las versiones mal clasificadas, con `versionId`, la `kind` correcta y `reason` (una frase en español neutro). Si todo está bien, responde una lista vacía. En la duda, no cambies. Usa solo los ids de la entrada.
