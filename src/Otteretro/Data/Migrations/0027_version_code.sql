-- #46 (decisión 278 de Otteretro): el TitleID o el serial de cada versión, para que Otteretro la reconozca por su ID antes que por
-- su nombre. Una versión puede tener varios (uno por región).
CREATE TABLE catalog.version_code (
    version_id INTEGER NOT NULL,
    code TEXT NOT NULL,
    PRIMARY KEY (version_id, code)
);
CREATE INDEX catalog.version_code_code ON version_code (code);
