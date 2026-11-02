-- Respuestas de IGDB y demás fuentes (F1.3): para no repetir llamadas y respetar sus límites.
-- key = fuente, método, URL y cuerpo de la consulta (sin claves). No va en el respaldo (S3).
CREATE TABLE api_cache (
    key        TEXT PRIMARY KEY,
    response   TEXT NOT NULL,
    expires_at TEXT NOT NULL
);
