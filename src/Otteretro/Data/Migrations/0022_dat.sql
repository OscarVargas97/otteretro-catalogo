-- #179: los DAT de No-Intro y Redump que publica libretro (`libretro-database`), para verificar por hash e identificar por
-- serial sin conexión. Son datos públicos, sin nada tuyo: van en el catálogo.
CREATE TABLE catalog.dat_entry (
    console TEXT NOT NULL,
    source  TEXT NOT NULL CHECK (source IN ('no-intro', 'redump')),
    name    TEXT NOT NULL,
    sha1    TEXT,
    crc     TEXT,
    size    INTEGER,
    serial  TEXT,
    region  TEXT
);
CREATE INDEX catalog.dat_entry_sha1 ON dat_entry (sha1);
CREATE INDEX catalog.dat_entry_console ON dat_entry (console, source);
