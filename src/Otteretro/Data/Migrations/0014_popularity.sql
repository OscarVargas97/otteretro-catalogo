-- F2.17 (#89): popularidad de cada juego en IGDB (total_rating_count), para ordenar por «más reconocidas» sin Claude.
-- NULL = todavía no se pidió; la tarea igdb.popularity la completa (la encola el arranque, IgdbImport.EnqueuePopularity).
ALTER TABLE catalog.game ADD COLUMN popularity INTEGER;
