-- #97 (decisiones 354 y 355 de Otteretro): los repos que la tarea «Emuladores» ya miró como posibles forks, para no repetirlos ni volver
-- a pedirle a Claude lo que ya resolvió. repo: «host/dueño/repo» en minúsculas. result: filtered (no pasó los filtros; se vuelve a
-- mirar pasado un tiempo, por si publicó una release), review (fue a Revisar) o rejected (Claude dijo que no es un fork de una receta).
-- Va en la base propia, como review_item: es el trabajo de la administración, no algo que se publica. (La 0031 es de otra rama.)
CREATE TABLE fork_seen (
    repo       TEXT PRIMARY KEY,
    result     TEXT NOT NULL CHECK (result IN ('filtered', 'review', 'rejected')),
    reason     TEXT,
    checked_at TEXT NOT NULL
);
