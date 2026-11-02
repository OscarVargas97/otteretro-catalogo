-- Tablas y columnas en inglés, como el código (#139). La 0001 no cambia: ya está aplicada en las bases existentes.
ALTER TABLE equipo RENAME TO machine;
ALTER TABLE machine RENAME COLUMN nombre TO name;
ALTER TABLE machine RENAME COLUMN so TO os;

-- machine_id vacío = el ajuste vale para todos los equipos.
ALTER TABLE ajuste RENAME TO setting;
ALTER TABLE setting RENAME COLUMN clave TO name;
ALTER TABLE setting RENAME COLUMN valor TO value;
ALTER TABLE setting RENAME COLUMN equipo_id TO machine_id;
