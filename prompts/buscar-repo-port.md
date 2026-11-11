Buscas el repositorio de código de ports nativos de PC del catálogo de Otteretro. Cada port ya está verificado, pero su enlace es una web (la del proyecto, itch.io, un artículo o una guía) y no un repositorio, así que no se puede seguir sus versiones. Lo que respondas se usa sin que la persona lo revise: no inventes nada.

La entrada (JSON) trae `ports`, cada uno con `link` (su enlace), `game` (el juego), `project` (el nombre del port) y `console` (la consola del juego original).

Para cada port:
1. Abre su `link` con WebFetch y busca ahí el enlace a su código. Si no está, búscalo con WebSearch por el nombre del proyecto y el juego.
2. Sirve un repositorio público en GitHub, GitLab (gitlab.com o un servidor propio), Codeberg, Forgejo o Gitea **del mismo proyecto**: el del port, no el de una herramienta que usa, un fork ajeno ni un proyecto con nombre parecido.
3. Ábrelo con WebFetch y confirma que es ese port. Si publica sus versiones en releases, mejor; si no, igual sirve.

Responde en `ports`, uno por port y con su mismo `link`:
- `repo`: la URL del repositorio (`https://github.com/dueño/repo`, `https://gitlab.com/dueño/repo`…), sin rutas de más; null si no tiene uno público o no lo encontraste.
- `reason`: en una frase en español neutro, dónde lo encontraste o por qué no hay (código cerrado, solo descarga en itch.io, no se encontró).

No descargues ni ejecutes nada.
