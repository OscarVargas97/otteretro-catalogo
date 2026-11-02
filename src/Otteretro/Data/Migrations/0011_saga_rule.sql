-- F2.12 (#82). learning.task_id: la tarea que hizo el cambio, para agruparlos en Revisar › Aprendizajes; los de antes se
-- asignan a la depuración o carga que estaba corriendo cuando se hicieron.
ALTER TABLE learning ADD COLUMN task_id INTEGER;
UPDATE learning SET task_id = (
    SELECT max(t.id) FROM task t WHERE t.kind IN ('claude.cleanup', 'claude.load') AND t.created_at <= learning.created_at)
WHERE json_extract(content, '$.prompt') = 'depurar-sagas';

-- «Corregir» un cambio de Claude: la nota de la persona queda como regla de la saga y va en la próxima depuración.
CREATE TABLE saga_rule (
    id          INTEGER PRIMARY KEY,
    saga_id     INTEGER NOT NULL,
    text        TEXT NOT NULL,
    learning_id INTEGER,
    created_at  TEXT NOT NULL
);
CREATE INDEX saga_rule_saga ON saga_rule (saga_id);
