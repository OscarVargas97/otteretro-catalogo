-- Lo que prepara Claude (F2.3, arquitectura §4). Rutas de cada saga: lanzamiento, historia y comunidad, con sus
-- fuentes; approved = la aprobaste (o la editaste) en Revisar.
CREATE TABLE route (
    id        INTEGER PRIMARY KEY,
    saga_id   INTEGER NOT NULL REFERENCES saga(id) ON DELETE CASCADE,
    kind      TEXT NOT NULL CHECK (kind IN ('release', 'story', 'community')),
    rationale TEXT,
    sources   TEXT, -- JSON: [{ "title", "url" }]
    approved  INTEGER NOT NULL DEFAULT 0,
    UNIQUE (saga_id, kind)
);

CREATE TABLE route_step (
    route_id               INTEGER NOT NULL REFERENCES route(id) ON DELETE CASCADE,
    position               INTEGER NOT NULL,
    game_id                INTEGER NOT NULL REFERENCES game(id) ON DELETE CASCADE,
    recommended_version_id INTEGER REFERENCES version(id) ON DELETE SET NULL,
    reason                 TEXT,
    PRIMARY KEY (route_id, position)
);

-- Lo pendiente de revisar (Revisar): identificaciones dudosas, sin identificar y resultados de Claude por aprobar.
-- entity: qué toca (saga, game, version, route…); data: el detalle como JSON.
CREATE TABLE review_item (
    id         INTEGER PRIMARY KEY,
    kind       TEXT NOT NULL CHECK (kind IN ('doubtful', 'unidentified', 'pending_approval')),
    entity     TEXT NOT NULL,
    entity_id  INTEGER,
    data       TEXT,
    status     TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'approved', 'rejected')),
    created_at TEXT NOT NULL
);
CREATE INDEX review_item_open ON review_item (status, kind);

-- Lo que resolvió Claude, guardado como dato para que el script lo repita (§11.1). undone = lo deshiciste.
CREATE TABLE learning (
    id         INTEGER PRIMARY KEY,
    kind       TEXT NOT NULL CHECK (kind IN ('alias', 'rule', 'recipe', 'route')),
    content    TEXT NOT NULL, -- JSON
    created_at TEXT NOT NULL,
    undone     INTEGER NOT NULL DEFAULT 0
);

-- Una fila por llamada a `claude -p` (§11.4). result: ok, invalid (no tenía la forma pedida), failed o limit.
-- cost_usd (el que informa `claude`, pondera el modelo) estima cuánto de cada ventana de la suscripción se usó (tope por ventana, F2.2).
CREATE TABLE claude_usage (
    id                    INTEGER PRIMARY KEY,
    task_id               INTEGER,
    prompt                TEXT NOT NULL,
    model                 TEXT,
    duration_ms           INTEGER NOT NULL,
    result                TEXT NOT NULL CHECK (result IN ('ok', 'invalid', 'failed', 'limit')),
    input_tokens          INTEGER NOT NULL DEFAULT 0,
    output_tokens         INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
    cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd              REAL NOT NULL DEFAULT 0,
    created_at            TEXT NOT NULL
);
CREATE INDEX claude_usage_created ON claude_usage (created_at);

-- Huella de los datos con los que se pidió cada resultado (F2.4): si no cambió, no se vuelve a pedir a Claude.
-- key: de qué es el resultado («saga:12»). Borrar la fila = «recalcular».
CREATE TABLE claude_fingerprint (
    prompt     TEXT NOT NULL,
    key        TEXT NOT NULL,
    input_hash TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (prompt, key)
);
