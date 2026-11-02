-- #158 (#201, #203): un disco es una ubicación de tipo disk con ruta X:\Otteretro y cinco carpetas fijas adentro. Las de un
-- solo tipo (#198) quedan hasta que se pasan al estándar (#159). SQLite no cambia un CHECK: la columna se rehace.
ALTER TABLE library ADD COLUMN old_type TEXT;
UPDATE library SET old_type = type;
ALTER TABLE library DROP COLUMN type;
ALTER TABLE library ADD COLUMN type TEXT NOT NULL DEFAULT 'disk' CHECK (type IN ('disk', 'game', 'emulator', 'port', 'complement', 'system'));
UPDATE library SET type = old_type;
ALTER TABLE library DROP COLUMN old_type;
-- El disco predeterminado (★): ahí va lo nuevo y Herramientas (#204).
ALTER TABLE library ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;
