-- #69 (decisiones 299 y 300 de Otteretro): los ports nativos de PC que existen, en su propia lista e independiente de los juegos.
-- Salen de Recompendium, de los extras del catálogo y de la búsqueda de nuevos. Un paso aparte cruza cada uno con su juego
-- (`game_id`); lo que no cruza queda sin juego y se asigna a mano (`manual`). Solo se publica lo que tiene juego.
CREATE TABLE catalog.port (
    id INTEGER PRIMARY KEY,
    -- La consola de la que sale el juego (id de `console`; la clave de la fuente si el catálogo no la tiene).
    console TEXT NOT NULL,
    -- El juego como lo nombra la fuente: con eso se cruza.
    game TEXT NOT NULL,
    project TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('decomp', 'recomp', 'remake', 'port', 'native-layer', 'engine')),
    -- La herramienta de un recomp (N64Recomp, ReXGlue…).
    toolchain TEXT,
    status TEXT NOT NULL CHECK (status IN ('complete', 'playable', 'beta', 'experimental')),
    abandoned INTEGER NOT NULL DEFAULT 0,
    link TEXT NOT NULL,
    -- recompendium, extra o la página donde lo encontró la búsqueda.
    source TEXT NOT NULL,
    -- Cuándo entró a la lista (AAAA-MM-DD): lo que muestra Otteretro en Novedades.
    added TEXT NOT NULL,
    -- Si la fuente lo trae, se cruza por él antes que por el nombre.
    igdb_id INTEGER,
    -- El emulador de referencia de su consola y cómo corre el juego en él (3 perfecto, 2 jugable, 1 parcial, 0 no funciona;
    -- null sin dato o sin lista por juego).
    emulator TEXT,
    emulator_level INTEGER CHECK (emulator_level BETWEEN 0 AND 3),
    game_id INTEGER,
    -- El juego lo eligió la persona: el cruce no lo cambia.
    manual INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX catalog.port_game ON port (game_id);
