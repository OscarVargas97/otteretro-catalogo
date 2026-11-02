-- #210: los enlaces capturados, como en JDownloader. Son datos privados de la persona: van en `otteretro.db`, no en `catalogo.db`, y nunca se comparten (decisión 245).
CREATE TABLE download_link (
    id INTEGER PRIMARY KEY,
    url TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    package TEXT NOT NULL,
    site TEXT,
    size INTEGER,
    state TEXT NOT NULL DEFAULT 'unchecked',
    detail TEXT,
    status TEXT NOT NULL DEFAULT 'captured',
    -- El juego (desde su ficha) o la versión que se identificó. El catálogo es otra base: sin claves foráneas hacia él.
    game_id INTEGER,
    version_id INTEGER,
    task_id INTEGER,
    created_at TEXT NOT NULL
);
CREATE INDEX download_link_game ON download_link(game_id);
CREATE INDEX download_link_status ON download_link(status);

-- De qué descarga salió un archivo: cuando se identifica su juego, por el camino que sea, sus enlaces quedan en la ficha.
ALTER TABLE source ADD COLUMN download_task INTEGER;
CREATE TRIGGER source_download_game AFTER UPDATE OF version_id, download_task ON source
WHEN NEW.version_id IS NOT NULL AND NEW.download_task IS NOT NULL
BEGIN
    UPDATE download_link SET version_id = NEW.version_id WHERE task_id = NEW.download_task AND version_id IS NULL;
END;
