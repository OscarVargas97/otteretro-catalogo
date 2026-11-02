-- Orígenes de cada versión (arquitectura §4): un archivo en una biblioteca (F3) o tu cuenta en una tienda de PC.
-- Lo de una tienda que todavía no está en el catálogo queda igual, con su nombre y sin versión, hasta que se enlaza
-- (todo lo que tienes aparece siempre, §6.1). playtime (minutos) y last_played: lo importado de la tienda (#133).
CREATE TABLE source (
    id            INTEGER PRIMARY KEY,
    version_id    INTEGER REFERENCES version(id) ON DELETE SET NULL,
    kind          TEXT NOT NULL CHECK (kind IN ('file', 'store')),
    library_id    TEXT,
    relative_path TEXT,
    component     TEXT CHECK (component IN ('BASE', 'UPD', 'DLC')),
    title_id      TEXT,
    size          INTEGER,
    sha1          TEXT,
    verified      INTEGER NOT NULL DEFAULT 0,
    store         TEXT,
    store_id      TEXT,
    name          TEXT,
    family_shared INTEGER NOT NULL DEFAULT 0,
    playtime      INTEGER,
    last_played   TEXT
);
CREATE INDEX source_version ON source (version_id);
CREATE UNIQUE INDEX source_store ON source (store, store_id) WHERE kind = 'store';
