-- Claves y tokens cifrados de cada equipo (§5.6): lo que cifra un PC no lo abre otro.
-- id = servicio.campo (steam.apiKey, igdb.clientSecret…); value = el valor cifrado.
CREATE TABLE credential (
    machine_id TEXT NOT NULL REFERENCES machine(id),
    id         TEXT NOT NULL,
    value      BLOB NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (machine_id, id)
);
