-- #94 (decisión 349 de Otteretro): las recetas de emuladores, ports, complementos y herramientas viajan en lo publicado. `json` es la
-- receta entera (recetas/receta.schema.json); `kind` y `recipe_console` sirven para listarlas sin abrir cada una.
CREATE TABLE catalog.recipe (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    json TEXT NOT NULL
);
-- Las consolas que corre (`consoles` de la receta), con el id de consola del catálogo.
CREATE TABLE catalog.recipe_console (
    recipe_id TEXT NOT NULL,
    console_id TEXT NOT NULL,
    PRIMARY KEY (recipe_id, console_id)
);
CREATE INDEX catalog.recipe_console_console ON recipe_console (console_id);
