-- otteretro-catalogo#198: los ports que la tarea «Archivo del juego» ya vio, con el asset que tenían. No se vuelven a pedir a Claude
-- hasta que cambie su asset. result: set (se guardó gameFile), doubtful (aviso en su tarjeta) o unknown (no se supo).
CREATE TABLE gamefile_seen (
    recipe     TEXT PRIMARY KEY,
    asset      TEXT NOT NULL,
    result     TEXT NOT NULL,
    reason     TEXT,
    checked_at TEXT NOT NULL
);
