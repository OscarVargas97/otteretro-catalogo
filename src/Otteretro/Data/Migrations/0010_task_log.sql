-- Registro de cada tarea (F2.10, #80): qué hizo, cambió o bajó, y cuándo esperó o falló. message: código y parámetros
-- para el front (#142), como JSON. Se borra con su tarea («Limpiar terminadas», #81).
CREATE TABLE task_log (
    id      INTEGER PRIMARY KEY,
    task_id INTEGER NOT NULL,
    at      TEXT NOT NULL,
    level   TEXT NOT NULL CHECK (level IN ('info', 'warn', 'error')),
    message TEXT NOT NULL
);
CREATE INDEX task_log_task ON task_log (task_id, id);
