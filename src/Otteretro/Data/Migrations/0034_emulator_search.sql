-- #130 (plan de #128): la última vez que la tarea «Mejor emulador» buscó para un juego. Va en la base propia, como review_item: es el
-- trabajo de la administración, no algo que se publica. Las recomendaciones van en review_item (entity 'emulatorPick', entity_id = el juego).
CREATE TABLE emulator_search (
    game_id     INTEGER PRIMARY KEY,
    searched_at TEXT NOT NULL
);
