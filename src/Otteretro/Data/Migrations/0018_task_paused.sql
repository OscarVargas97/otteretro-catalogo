-- Pausar, reanudar y cancelar desde el panel (#143, #146): estados nuevos `paused` (la cola y el arranque no la toman hasta
-- reanudarla) y `canceled` (terminada a pedido). SQLite no cambia un CHECK: se rehace la tabla con lo mismo.
CREATE TABLE task_new (
    id          INTEGER PRIMARY KEY,
    kind        TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'running', 'paused', 'done', 'error', 'canceled')),
    progress    INTEGER NOT NULL DEFAULT 0,
    detail      TEXT,
    data        TEXT,
    resume_at   TEXT,
    attempts    INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    finished_at TEXT
);
INSERT INTO task_new (id, kind, status, progress, detail, data, resume_at, attempts, created_at, finished_at)
    SELECT id, kind, status, progress, detail, data, resume_at, attempts, created_at, finished_at FROM task;
DROP TABLE task;
ALTER TABLE task_new RENAME TO task;
CREATE INDEX task_status ON task (status);
