-- #106: las versiones de una consola (Colección, Consolas, el filtro de plataforma de Sagas) se buscaban recorriendo toda la
-- tabla. Con este índice, contar los juegos de una consola o ver si un juego sale en ella no lee nada más.
CREATE INDEX catalog.version_platform ON version (platform, game_id);
