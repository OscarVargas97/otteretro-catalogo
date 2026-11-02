-- Registro de bibliotecas de juegos (§5.2, F3.1). El id es el del marcador otteretro-biblioteca.json y no cambia nunca;
-- path es la última ruta conocida. kind: local o network (SMB/NFS, #136). Quitar una biblioteca borra la fila, nunca archivos.
CREATE TABLE library (
    id         TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    kind       TEXT NOT NULL CHECK (kind IN ('local', 'network')),
    path       TEXT NOT NULL,
    created_at TEXT NOT NULL
);
