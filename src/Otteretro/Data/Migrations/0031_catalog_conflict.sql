-- #104: lo que quedó en conflicto al aplicar una versión publicada del catálogo, para elegir en Ajustes › Catálogo. Va en la base propia:
-- es del generador, no se publica. Los valores son JSON (null, número o texto); `field` vacío es la fila entera (borrada en un lado y
-- cambiada en el otro), y entonces base, local y remote son la fila como objeto, o null si no existe en ese lado.
CREATE TABLE catalog_conflict (
    id         INTEGER PRIMARY KEY,
    version    TEXT NOT NULL,
    table_name TEXT NOT NULL,
    row_key    TEXT NOT NULL,
    field      TEXT,
    base       TEXT,
    local      TEXT,
    remote     TEXT
);
