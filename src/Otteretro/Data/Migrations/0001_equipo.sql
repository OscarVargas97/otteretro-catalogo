-- Equipos donde se usa Otteretro y ajustes (globales o por equipo).
CREATE TABLE equipo (
    id     TEXT PRIMARY KEY,
    nombre TEXT NOT NULL,
    so     TEXT NOT NULL
);

-- equipo_id vacío = el ajuste vale para todos los equipos.
CREATE TABLE ajuste (
    clave     TEXT NOT NULL,
    valor     TEXT NOT NULL,
    equipo_id TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (clave, equipo_id)
);
