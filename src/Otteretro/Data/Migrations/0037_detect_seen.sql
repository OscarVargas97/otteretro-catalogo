-- otteretro-catalogo#174: el detect de cada receta que la tarea «Emuladores» ya comprobó contra el archivo de su release. Se vuelve a
-- comprobar cuando cambia la release (tag) o el detect.
CREATE TABLE detect_seen (
    recipe     TEXT PRIMARY KEY,
    tag        TEXT NOT NULL,
    detect     TEXT NOT NULL,
    checked_at TEXT NOT NULL
);
