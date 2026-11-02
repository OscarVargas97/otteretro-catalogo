-- #149 (#198, #199): cada ubicación guarda un tipo de cosa: juegos, emuladores, ports, complementos o sistema (BIOS, keys).
-- Las que ya había son de juegos. La tabla sigue llamándose library: «ubicación» es el nombre que ve la persona.
ALTER TABLE library ADD COLUMN type TEXT NOT NULL DEFAULT 'game' CHECK (type IN ('game', 'emulator', 'port', 'complement', 'system'));

-- #195: la ubicación de juegos donde caen los juegos nuevos de cada consola (la usa la ingesta, F10). Va en la base
-- principal y no en el catálogo: es una elección de la persona.
CREATE TABLE console_destination (
    console_id TEXT PRIMARY KEY,
    library_id TEXT NOT NULL REFERENCES library(id) ON DELETE CASCADE
);
