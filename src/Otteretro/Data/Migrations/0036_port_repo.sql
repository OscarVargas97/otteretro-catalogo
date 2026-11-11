-- #155: el repo de un port cuyo enlace es una web (itch.io, la web del proyecto, un artículo), que busca Claude en «Buscar ports nuevos».
-- La receta del port sale de ahí; el enlace sigue siendo el del port.
ALTER TABLE catalog.port ADD COLUMN repo TEXT;
