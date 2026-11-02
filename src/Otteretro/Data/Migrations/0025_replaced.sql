-- #3: lo que dejó de existir en el catálogo y con qué se reemplazó. Se publica: la app pasa lo de cada persona al juego nuevo.
CREATE TABLE catalog.replaced (
    kind TEXT NOT NULL CHECK (kind IN ('game', 'version')),
    old_id INTEGER NOT NULL,
    new_id INTEGER,
    PRIMARY KEY (kind, old_id)
);
