-- #112: los juegos icónicos de cada saga (los que propuso Claude y aprobaste), para elegir de cuál sale la imagen de la saga.
-- Antes solo se guardaba que la saga es icónica: los ya aprobados se recuperan de lo guardado en Revisar.
ALTER TABLE catalog.game ADD COLUMN iconic INTEGER NOT NULL DEFAULT 0;
UPDATE catalog.game SET iconic = 1 WHERE id IN (
    SELECT j.value FROM review_item r, json_each(r.data, '$.result.gameIds') j
    WHERE r.status = 'approved' AND json_extract(r.data, '$.prompt') = 'iconico' AND json_extract(r.data, '$.result.iconic'));
