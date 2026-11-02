// Navegación espacial con mando (pantallas.md §0.7): desde el elemento con foco, el más cercano en esa dirección.

export type Direction = "up" | "down" | "left" | "right";

type Box = { left: number; right: number; top: number; bottom: number };

/**
 * Elige el candidato más cercano hacia `dir`. Primero cuenta la distancia en esa dirección (borde a borde) y,
 * con el doble de peso, cuánto se desvía hacia el costado; si se superponen en el otro eje, no se desvía nada.
 * Devuelve el índice, o -1 si no hay nada en esa dirección.
 */
export function nearest(from: Box, candidates: Box[], dir: Direction): number {
  let best = -1;
  let bestScore = Infinity;
  candidates.forEach((c, i) => {
    const ahead =
      dir === "right" ? c.left - from.right : dir === "left" ? from.left - c.right : dir === "down" ? c.top - from.bottom : from.top - c.bottom;
    // Un poco de tolerancia: elementos que se tocan o se pisan por 1 px siguen contando.
    if (ahead < -1) return;
    const horizontal = dir === "left" || dir === "right";
    const [a1, a2, b1, b2] = horizontal ? [from.top, from.bottom, c.top, c.bottom] : [from.left, from.right, c.left, c.right];
    const aside = b2 < a1 ? a1 - b2 : b1 > a2 ? b1 - a2 : 0;
    const score = Math.max(ahead, 0) + aside * 2;
    if (score < bestScore) {
      bestScore = score;
      best = i;
    }
  });
  return best;
}
