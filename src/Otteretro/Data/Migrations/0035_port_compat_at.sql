-- #153: cuándo se consultó la compatibilidad del juego de cada port. La tarea «Ports» solo vuelve a consultar lo nuevo o lo de hace más de
-- un mes (las fuentes juego a juego tardan: Xbox 360 y PS4 van por la búsqueda de GitHub).
ALTER TABLE catalog.port ADD COLUMN compat_at TEXT;
