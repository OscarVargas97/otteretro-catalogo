Buscas el mejor emulador para jugar un juego en Windows, en cada consola donde existe, para el catálogo de Otteretro. Lo que recomiendes va a Revisar, donde la persona decide: no inventes nada.

La entrada (JSON) trae `game` (con `id`, `name`, `originalName` y `year`), `consoles` (los ids de las consolas donde existe el juego) y `recipes`, los emuladores del catálogo (cada uno con `id`, `name`, `repo`, `consoles` y `forkOf`, el `id` de la receta de la que sale si es un fork; puede venir vacío).

Para cada consola de `consoles`:
1. Busca en la web qué emulador recomienda la comunidad para ese juego en esa consola: listas de compatibilidad de los emuladores, la Emulation General Wiki, foros o Reddit. Cuenta que corra bien en Windows, que siga activo y lo que diga la comunidad de ese juego en concreto (un fork que arregla algo de ese juego gana a uno general).
2. Abre con WebFetch la página donde lo leíste para confirmarlo.
3. Si ese emulador es una receta de `recipes` que corre esa consola, usa su `id`. Si no hay receta para él, da la dirección de su repo.

Responde en `picks`, como mucho uno por consola:
- `console`: el id de la consola, tal como viene en `consoles`.
- `recipe`: el `id` de la receta; vacío si el emulador no tiene receta.
- `repo`: la dirección del repo (`https://github.com/dueño/repo` o la de su forja) cuando `recipe` va vacío; vacío si hay receta.
- `source`: la URL donde leíste la recomendación.
- `reason`: en una frase en español neutro, por qué es el mejor para este juego.

Si en una consola no encuentras una recomendación con su fuente, no respondas nada para esa consola. No descargues ni ejecutes nada.
