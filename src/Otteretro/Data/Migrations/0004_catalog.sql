-- Catálogo, seguimiento y tiendas (arquitectura §4, F1.4).

-- Las 23 consolas y PC. id = nombre corto (lo usan la CLI y version.platform).
-- emulation_status (good/playable/early/none) es un dato que se actualiza; esta es la semilla.
CREATE TABLE console (
    id               TEXT PRIMARY KEY,
    name             TEXT NOT NULL,
    company          TEXT,
    emulation_status TEXT CHECK (emulation_status IN ('good', 'playable', 'early', 'none'))
);

INSERT INTO console (id, name, company, emulation_status) VALUES
    ('nes',     'NES',                'Nintendo',  'good'),
    ('gb',      'Game Boy',           'Nintendo',  'good'),
    ('snes',    'SNES',               'Nintendo',  'good'),
    ('vb',      'Virtual Boy',        'Nintendo',  'good'),
    ('n64',     'Nintendo 64',        'Nintendo',  'good'),
    ('gba',     'Game Boy Advance',   'Nintendo',  'good'),
    ('gc',      'GameCube',           'Nintendo',  'good'),
    ('ds',      'Nintendo DS',        'Nintendo',  'good'),
    ('wii',     'Wii',                'Nintendo',  'good'),
    ('3ds',     'Nintendo 3DS',       'Nintendo',  'good'),
    ('wiiu',    'Wii U',              'Nintendo',  'good'),
    ('switch',  'Switch',             'Nintendo',  'good'),
    ('ps1',     'PlayStation',        'Sony',      'good'),
    ('ps2',     'PlayStation 2',      'Sony',      'good'),
    ('psp',     'PSP',                'Sony',      'good'),
    ('ps3',     'PlayStation 3',      'Sony',      'playable'),
    ('vita',    'PS Vita',            'Sony',      'playable'),
    ('ps4',     'PlayStation 4',      'Sony',      'early'),
    ('ps5',     'PlayStation 5',      'Sony',      'early'),
    ('xbox',    'Xbox',               'Microsoft', 'playable'),
    ('x360',    'Xbox 360',           'Microsoft', 'playable'),
    ('xone',    'Xbox One',           'Microsoft', 'none'),
    ('xseries', 'Xbox Series X|S',    'Microsoft', 'none'),
    ('pc',      'PC',                 NULL,        NULL);

-- La saga no tiene plataforma: va en la versión (#131). chosen_route apunta a route (F2).
-- igdb_kind: franchise o collection (en IGDB son listas aparte y sus ids se repiten).
CREATE TABLE saga (
    id           INTEGER PRIMARY KEY,
    name         TEXT NOT NULL,
    igdb_kind    TEXT CHECK (igdb_kind IN ('franchise', 'collection')),
    igdb_id      INTEGER,
    favorite     INTEGER NOT NULL DEFAULT 0,
    following    INTEGER NOT NULL DEFAULT 0,
    iconic       INTEGER NOT NULL DEFAULT 0,
    chosen_route INTEGER,
    UNIQUE (igdb_kind, igdb_id)
);

-- saga_id vacío = el juego va en «Emulación» (sin saga).
CREATE TABLE game (
    id            INTEGER PRIMARY KEY,
    saga_id       INTEGER REFERENCES saga(id),
    name_es       TEXT NOT NULL,
    original_name TEXT,
    year          INTEGER,
    igdb_id       INTEGER UNIQUE
);
CREATE INDEX game_saga ON game (saga_id);

-- igdb_id: la ficha de IGDB de la que sale (la del remake o el port, si lo es). steam_app_id: solo en PC.
CREATE TABLE version (
    id              INTEGER PRIMARY KEY,
    game_id         INTEGER NOT NULL REFERENCES game(id) ON DELETE CASCADE,
    platform        TEXT NOT NULL REFERENCES console(id),
    kind            TEXT NOT NULL DEFAULT 'original' CHECK (kind IN ('original', 'remaster', 'remake')),
    year            INTEGER,
    export_to_steam INTEGER NOT NULL DEFAULT 0,
    igdb_id         INTEGER,
    steam_app_id    INTEGER,
    UNIQUE (igdb_id, platform)
);
CREATE INDEX version_game ON version (game_id);
CREATE INDEX version_steam ON version (steam_app_id);

CREATE TABLE version_criterion (
    version_id INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    criterion  TEXT NOT NULL CHECK (criterion IN ('content', 'gameplay', 'technical', 'original')),
    value      TEXT,
    text       TEXT,
    PRIMARY KEY (version_id, criterion)
);

-- Un valor por campo y fuente; la prioridad por campo la aplica F1.6. Lo manual nunca se sobrescribe.
CREATE TABLE field_value (
    entity    TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    field     TEXT NOT NULL,
    value     TEXT,
    source    TEXT NOT NULL,
    manual    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (entity, entity_id, field, source)
);

-- Seguimiento de cada juego (#133).
CREATE TABLE progress (
    game_id     INTEGER PRIMARY KEY REFERENCES game(id) ON DELETE CASCADE,
    status      TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'playing', 'finished', 'completed', 'dropped')),
    started_at  TEXT,
    finished_at TEXT,
    rating      INTEGER CHECK (rating BETWEEN 1 AND 10),
    notes       TEXT,
    favorite    INTEGER NOT NULL DEFAULT 0
);

-- Sesiones de juego (#133); las llena F4.
CREATE TABLE play_session (
    id         INTEGER PRIMARY KEY,
    version_id INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    machine_id TEXT NOT NULL REFERENCES machine(id),
    launcher   TEXT NOT NULL CHECK (launcher IN ('steam', 'heroic', 'lutris', 'itch', 'otteretro')),
    started_at TEXT NOT NULL,
    ended_at   TEXT,
    duration   INTEGER
);
CREATE INDEX play_session_version ON play_session (version_id);

CREATE TABLE price_watch (
    game_id INTEGER PRIMARY KEY REFERENCES game(id) ON DELETE CASCADE
);

-- «No me interesa» de una saga, hasta una fecha (6 meses, S8).
CREATE TABLE dismissal (
    saga_id INTEGER PRIMARY KEY REFERENCES saga(id) ON DELETE CASCADE,
    until   TEXT NOT NULL
);
