-- #178: el escaneo de Colección. Cada archivo guarda su consola (lo que no se reconoce también la necesita, para Revisar) y
-- aparece una sola vez por ubicación y ruta: volver a escanear lo actualiza.
ALTER TABLE source ADD COLUMN console TEXT;
CREATE UNIQUE INDEX source_file ON source (library_id, relative_path) WHERE kind = 'file';
