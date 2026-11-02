"use client";

import { useState } from "react";
import { imageUrl } from "@/lib/client";

/** El tono de cada héroe ya visto: se calcula una vez (#112). null = sin color propio, queda el acento base. */
const hues = new Map<number, number | null>();

/** El tono dominante de una imagen: el de los píxeles con más color, pesados por su saturación. Gris u oscura → null. */
function dominantHue(img: HTMLImageElement): number | null {
  const canvas = document.createElement("canvas");
  canvas.width = 48; canvas.height = 16;
  const g = canvas.getContext("2d", { willReadFrequently: true });
  if (!g) return null;
  g.drawImage(img, 0, 0, canvas.width, canvas.height);
  let data: Uint8ClampedArray;
  try { data = g.getImageData(0, 0, canvas.width, canvas.height).data; } catch { return null; }
  const bins = new Array<number>(12).fill(0);
  const sums = new Array<number>(12).fill(0);
  for (let i = 0; i < data.length; i += 4) {
    const r = data[i] / 255, gr = data[i + 1] / 255, b = data[i + 2] / 255;
    const max = Math.max(r, gr, b), min = Math.min(r, gr, b), l = (max + min) / 2, d = max - min;
    if (d === 0 || l < 0.15 || l > 0.9) continue;
    const s = d / (1 - Math.abs(2 * l - 1));
    if (s < 0.25) continue;
    let h = max === r ? ((gr - b) / d) % 6 : max === gr ? (b - r) / d + 2 : (r - gr) / d + 4;
    h = (h * 60 + 360) % 360;
    const bin = Math.floor(h / 30);
    bins[bin] += s;
    sums[bin] += h * s;
  }
  const best = bins.indexOf(Math.max(...bins));
  // Menos de un 5 % de la imagen con color: es gris.
  return bins[best] < data.length / 4 * 0.05 * 0.25 ? null : Math.round(sums[best] / bins[best]);
}

/**
 * El héroe de SteamGridDB detrás de una cabecera (#112, decisión #178) y el acento que sale de él. La imagen va de fondo y
 * aparece con un fundido: no cambia el alto de la cabecera, así que nada salta. Sin juego, el fondo y el acento de siempre.
 */
export function useHeroArt(gameId: number | null | undefined) {
  const [, redraw] = useState(0);
  const hue = gameId == null ? undefined : hues.get(gameId);
  const style = hue == null ? undefined : ({
    "--accent": `hsl(${hue} 78% 66%)`, "--accent-soft": `hsl(${hue} 40% 15%)`, "--on-accent": `hsl(${hue} 60% 8%)`,
  } as React.CSSProperties);
  const art = gameId == null ? null : (
    <div className="hero-art" aria-hidden="true">
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img key={gameId} src={imageUrl(gameId, "hero")} alt="" crossOrigin="anonymous" decoding="async"
        className={hues.has(gameId) ? "shown" : undefined}
        onLoad={(e) => {
          const img = e.currentTarget;
          img.classList.add("shown");
          if (!hues.has(gameId)) { hues.set(gameId, dominantHue(img)); redraw((n) => n + 1); }
        }}
        onError={(e) => { e.currentTarget.hidden = true; }} />
    </div>
  );
  return { art, style };
}
