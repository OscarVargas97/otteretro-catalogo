-- F2.13 (#83): las consolas se habilitan una a una desde las plataformas de IGDB. console_platform reemplaza el mapa fijo
-- del código (plataforma de IGDB → consola; las variantes van con su consola, como GBC con Game Boy).
-- enabled: si se carga y se actualiza (deshabilitar no borra sus juegos). refreshed_at: hasta cuándo trae IGDB (F2.14).
ALTER TABLE console ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE console ADD COLUMN refreshed_at TEXT;
UPDATE console SET refreshed_at = (
    SELECT max(t.created_at) FROM task t
    WHERE t.kind = 'igdb.platform' AND t.status = 'done' AND json_extract(t.data, '$.platform') = console.id);

CREATE TABLE console_platform (
    igdb_id    INTEGER PRIMARY KEY,
    console_id TEXT NOT NULL REFERENCES console (id)
);
INSERT INTO console_platform (igdb_id, console_id) VALUES
    (18, 'nes'),
    (33, 'gb'),
    (22, 'gb'),
    (19, 'snes'),
    (87, 'vb'),
    (4, 'n64'),
    (416, 'n64'),
    (24, 'gba'),
    (21, 'gc'),
    (20, 'ds'),
    (159, 'ds'),
    (5, 'wii'),
    (37, '3ds'),
    (137, '3ds'),
    (41, 'wiiu'),
    (130, 'switch'),
    (508, 'switch2'),
    (7, 'ps1'),
    (8, 'ps2'),
    (38, 'psp'),
    (9, 'ps3'),
    (46, 'vita'),
    (48, 'ps4'),
    (167, 'ps5'),
    (11, 'xbox'),
    (12, 'x360'),
    (49, 'xone'),
    (169, 'xseries'),
    (6, 'pc'),
    (161, 'pcvr'),
    (162, 'pcvr'),
    (163, 'pcvr'),
    (385, 'pcvr');
