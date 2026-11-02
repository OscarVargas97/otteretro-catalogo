-- #6: de dónde sale cada imagen de un juego (SteamGridDB o IGDB). Se publica la dirección, no la imagen: cada programa la baja al mostrarla.
CREATE TABLE catalog.image (
    game_id INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('cover', 'hero', 'logo')),
    url TEXT NOT NULL,
    PRIMARY KEY (game_id, kind)
);
