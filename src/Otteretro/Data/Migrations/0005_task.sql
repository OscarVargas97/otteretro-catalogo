-- Cola de tareas en segundo plano (F1.2). Una tarea en 'running' al arrancar se interrumpió: vuelve a 'queued'
-- y sigue desde `data`, el punto de control que guarda cada tipo. resume_at: no empieza antes (límites de las APIs).
-- detail: código y parámetros para el front (#142), como JSON.
CREATE TABLE task (
    id          INTEGER PRIMARY KEY,
    kind        TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'running', 'done', 'error')),
    progress    INTEGER NOT NULL DEFAULT 0,
    detail      TEXT,
    data        TEXT,
    resume_at   TEXT,
    attempts    INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    finished_at TEXT
);
CREATE INDEX task_status ON task (status);
