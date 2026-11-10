-- #119: los repos de ports que la tarea «Ports» ya miró para proponer una receta. repo: «host/dueño/repo» en minúsculas; tag: la
-- release que se miró. result: noWindows (sin archivo de Windows: se vuelve a mirar cuando cambie la release), review (espera en
-- Revisar), accepted o rejected (resuelto en Revisar: no vuelve a salir). Los candidatos van en review_item (entity 'port').
CREATE TABLE port_recipe_seen (
    repo       TEXT PRIMARY KEY,
    tag        TEXT,
    result     TEXT NOT NULL CHECK (result IN ('noWindows', 'review', 'accepted', 'rejected')),
    checked_at TEXT NOT NULL
);
