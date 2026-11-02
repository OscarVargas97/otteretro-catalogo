"use client";

// Piezas que comparten las pantallas del catálogo (F1.9): portadas, tarjetas, consolas y el id de la URL.
import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { imageUrl } from "@/lib/client";
import { consolesQuery } from "@/lib/queries";
import type { ConsoleCount, GameCard, SagaCard } from "@/lib/types";

/** Las 24 consolas con su nombre y cuántos juegos hay de cada una. */
export const useConsoles = () => useQuery(consolesQuery);

/** Nombre de una consola a partir de su id («n64» → «Nintendo 64»). */
export function useConsoleName() {
  const consoles = useConsoles().data;
  return (id: string) => consoles?.find((c) => c.id === id)?.name ?? id;
}

/** `?id=` de la URL (la exportación estática no admite rutas con parámetros). */
export function useIdParam() {
  return useSearchParams().get("id");
}

/** Tiempo jugado a partir de minutos: «14 h», «25 min» por debajo de una hora, o «—» si no hay. */
export function hours(minutes: number | string | null | undefined) {
  const m = Number(minutes ?? 0);
  return m <= 0 ? "—" : m < 60 ? `${m} min` : `${Math.round(m / 60)} h`;
}

// Tono de cada consola (#169): la portada que aún no se bajó es un degradado de su color.
const CONSOLE_HUE: Record<string, number> = {
  nes: 0, gb: 80, snes: 265, vb: 350, n64: 140, gba: 240, gc: 270, ds: 205, wii: 195, "3ds": 355, wiiu: 190, switch: 358, switch2: 358,
  ps1: 220, ps2: 228, psp: 232, ps3: 215, vita: 205, ps4: 222, ps5: 212, xbox: 110, x360: 100, xone: 120, xseries: 125, pc: 30, pcvr: 30,
};

/** Portada 2:3. Sin imagen de SteamGridDB, el nombre sobre un degradado del color de su consola. */
export function Cover({ gameId, name, hasCover, platform, style }: { gameId: number | string; name: string; hasCover: boolean; platform?: string | null; style?: React.CSSProperties }) {
  if (hasCover)
    return <div className="ot-cover" style={{ backgroundImage: `url("${imageUrl(gameId, "cover")}")`, backgroundSize: "cover", backgroundPosition: "center", ...style }} role="img" aria-label={name} />;
  const hue = platform ? CONSOLE_HUE[platform] : undefined;
  const tint = hue === undefined ? undefined : { background: `linear-gradient(160deg, hsl(${hue} 45% 34%), hsl(${hue} 35% 14%))` };
  return <div className="ot-cover" style={{ ...tint, ...style }}>{name}</div>;
}

// Nombre corto de cada consola para las tablas (#170); el nombre completo va en el título.
const SHORT: Record<string, string> = { switch: "Switch", switch2: "Switch 2", wii: "Wii", wiiu: "Wii U", vita: "Vita", xbox: "Xbox", x360: "X360", xone: "XOne", xseries: "XSX", pcvr: "PC VR" };

/** Consolas abreviadas, hasta `max` y «+N» con las demás en el título (#170). */
export function ShortPlatforms({ ids, max = 4 }: { ids: string[]; max?: number }) {
  const name = useConsoleName();
  const rest = ids.slice(max);
  return (
    <>
      {ids.slice(0, max).map((p) => <span key={p} className="ot-console" title={name(p)}>{SHORT[p] ?? p.toUpperCase()}</span>)}
      {rest.length > 0 && <span className="ot-console" title={rest.map(name).join(", ")}>+{rest.length}</span>}
    </>
  );
}

export function Platforms({ ids }: { ids: string[] }) {
  const name = useConsoleName();
  return <>{ids.map((p) => <span key={p} className="ot-console">{name(p)}</span>)}</>;
}

/** Tarjeta de juego (componente GameCard del sistema de diseño). */
export function GameTile({ game }: { game: GameCard }) {
  const t = useTranslations("catalog");
  const id = Number(game.id);
  return (
    <Link className={`ot-game${game.platforms.length === 0 ? " is-unplayable" : ""}`} href={`/game/?id=${id}`} style={{ textDecoration: "none", color: "var(--ink)" }}>
      <Cover gameId={id} name={game.name} hasCover={game.hasCover} platform={game.platforms[0]} />
      <p className="ot-game-name" title={game.name}>{game.name}</p>
      <div className="ot-game-meta">
        <Platforms ids={game.platforms} />
        {game.year && <span>{game.year}</span>}
      </div>
      <div className="ot-game-badges">
        {game.platforms.length === 0 && <span className="ot-badge">{t("noConsole")}</span>}
      </div>
    </Link>
  );
}

/** Barra de avance con «3 de 14» al lado (#170). `label` es lo que lee el lector de pantalla. */
export function Progress({ done, total, label }: { done: number; total: number; label: string }) {
  const t = useTranslations("catalog");
  return (
    <div className="progress-line" role="img" aria-label={label}>
      <div className="ot-progress"><span style={{ width: `${total > 0 ? (100 * done) / total : 0}%` }} /></div>
      <span className="small muted tabular" aria-hidden="true">{t("ofTotal", { done, total })}</span>
    </div>
  );
}

/** Tarjeta de saga (componente SagaCard). La id 0 es «Emulación», los juegos sin saga. */
export function SagaTile({ saga }: { saga: SagaCard }) {
  const t = useTranslations("catalog");
  const id = Number(saga.id);
  const name = id === 0 ? t("emulation") : saga.name;
  const cover = saga.coverGameId ? { backgroundImage: `url("${imageUrl(saga.coverGameId, "cover")}")`, backgroundSize: "cover", backgroundPosition: "center 20%" } : undefined;
  return (
    <Link className="ot-saga" href={`/saga/?id=${id}`} style={{ textDecoration: "none", color: "var(--ink)" }}>
      <div className="ot-saga-art" style={cover}><span className="saga-art-name">{name}</span></div>
      <div className="ot-saga-body">
        {/* Insignias (#170): ★, Icónica y Exclusiva de la empresa. La fila está siempre, así todas las tarjetas miden lo mismo. */}
        <div className="ot-saga-badges">
          {id === 0 && <span className="ot-badge">{t("noSaga")}</span>}
          {saga.iconic && <span className="ot-badge ot-badge-iconic">{t("iconic")}</span>}
          {saga.exclusive && <span className="ot-badge ot-badge-info">{t("exclusiveOf", { company: saga.exclusive })}</span>}
        </div>
        <div className="ot-saga-stats">
          <span>{t("games", { count: Number(saga.games) })}</span>
        </div>
        <div className="chips" style={{ gap: 4 }}><Platforms ids={saga.platforms} /></div>
      </div>
    </Link>
  );
}
