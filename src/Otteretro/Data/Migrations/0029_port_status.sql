-- #69 (respuestas de la persona del 2026-11-04): dos cambios a `port` sobre la 0028, que ya se aplicó en bases de prueba.
-- `status` suma no-port (una decompilación sin port todavía: se muestra, no se instala) y `delisted` marca lo que su fuente ya
-- no lista (se conserva y se sigue publicando). SQLite no cambia un CHECK en el lugar: tabla nueva, copiar, borrar y renombrar.
CREATE TABLE catalog.port_new (
    id INTEGER PRIMARY KEY,
    console TEXT NOT NULL,
    game TEXT NOT NULL,
    project TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('decomp', 'recomp', 'remake', 'port', 'native-layer', 'engine')),
    toolchain TEXT,
    -- complete: 100 % jugable de principio a fin; playable: jugable con fallos; beta; experimental; no-port: hay decompilación,
    -- pero todavía no un port.
    status TEXT NOT NULL CHECK (status IN ('complete', 'playable', 'beta', 'experimental', 'no-port')),
    abandoned INTEGER NOT NULL DEFAULT 0,
    delisted INTEGER NOT NULL DEFAULT 0,
    link TEXT NOT NULL,
    source TEXT NOT NULL,
    added TEXT NOT NULL,
    igdb_id INTEGER,
    emulator TEXT,
    emulator_level INTEGER CHECK (emulator_level BETWEEN 0 AND 3),
    game_id INTEGER,
    manual INTEGER NOT NULL DEFAULT 0
);
INSERT INTO catalog.port_new (id, console, game, project, kind, toolchain, status, abandoned, link, source, added, igdb_id, emulator, emulator_level, game_id, manual)
SELECT id, console, game, project, kind, toolchain, status, abandoned, link, source, added, igdb_id, emulator, emulator_level, game_id, manual FROM catalog.port;
DROP TABLE catalog.port;
ALTER TABLE catalog.port_new RENAME TO port;
CREATE INDEX catalog.port_game ON port (game_id);
