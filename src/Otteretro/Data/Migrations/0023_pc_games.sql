-- #182: un juego de PC suelto (`Juegos\PC\<Juego>`) es una carpeta; esto es su ejecutable principal, relativo a ella.
ALTER TABLE source ADD COLUMN executable TEXT;
