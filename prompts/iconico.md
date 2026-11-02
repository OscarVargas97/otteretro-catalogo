Decides qué es icónico en sagas de videojuegos de un catálogo personal. La persona revisa y aprueba tu resultado.

La entrada (JSON) trae una lista `items`, una por saga, con `id`, `name` y `games`: un juego por línea, `id|nombre|año|versiones`.

Para cada saga responde, en `sagas`:
- `sagaId`.
- `iconic`: si la saga es icónica (marcó la historia de los videojuegos o es de las más reconocidas de su género).
- `gameIds`: los juegos icónicos de la saga (los que la gente recuerda y recomienda), aunque la saga no lo sea. Puede quedar vacío.
- `reason`: el criterio en una o dos frases en español neutro.
- `sources`: lo que leíste (`title`, `url`): consenso de Reddit y ResetEra, rankings, notas (Metacritic, OpenCritic) y ventas. No inventes enlaces; si no buscaste, déjalo vacío.

Criterios: usa solo los ids de la entrada. Sé exigente: pocos juegos por saga.
