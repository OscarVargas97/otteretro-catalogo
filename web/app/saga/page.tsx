"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Suspense, useState } from "react";
import { Cover, ShortPlatforms, useConsoleName, useConsoles, useIdParam } from "@/components/Catalog";
import { PublishButton } from "@/components/Publish";
import { AddGame, GameActions, ROUTE_KINDS, RouteEditor, SagaDataEditor, type RouteKind } from "@/components/SagaEditing";
import { Tabs } from "@/components/Tabs";
import { useHeroArt } from "@/components/HeroArt";
import { Empty } from "@/components/Shell";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { PageSkeleton, Rows } from "@/components/Skeleton";
import { usePaged } from "@/lib/paged";
import { optimistic } from "@/lib/optimistic";
import { routesQuery, sagaQuery } from "@/lib/queries";
import type { GameCard, GamePage } from "@/lib/types";

// El detalle de una saga se abre como /saga/?id=… (0 = Emulación).
export default function SagaPage() {
  return <Suspense><Saga /></Suspense>;
}

/** Detalle de saga (§3.2): cabecera con favorita y seguir, la ruta que sigues (F2.6) y todos sus juegos. */
function Saga() {
  const t = useTranslations("catalog");
  const describe = useDescribeError();
  const id = useIdParam();
  const cache = useQueryClient();
  const consoles = useConsoles().data;
  const saga = useQuery({ ...sagaQuery(id ?? ""), enabled: id !== null });
  // Las rutas van a la par con la ficha y se muestran juntas: si llegaran después, empujarían la tabla (#105). «Emulación» no tiene.
  const routes = useQuery({ ...routesQuery(id ?? ""), enabled: id !== null && id !== "0" });
  // La tabla sigue por tramos al bajar (#177): «Emulación» tiene miles de juegos; la ficha trae los primeros 60.
  const hero = useHeroArt(saga.data?.heroGameId == null ? null : Number(saga.data.heroGameId));
  const more = usePaged(["saga", id, "games"], (offset) => `/api/sagas/${id}/games?offset=${offset}`, (p: GamePage) => ({ items: p.items, total: Number(p.total) }), id !== null);
  // Editar a mano (#17); una saga recién creada abre así.
  const [editing, setEditing] = useState(useSearchParams().get("edit") === "1");

  if (id === null) return <div className="content"><Empty title={t("saga.noneTitle")} text={t("saga.noneText")}><Link className="ot-btn ot-btn-primary" href="/sagas/">{t("goToSagas")}</Link></Empty></div>;
  if (saga.isError) return <div className="content"><p className="error" role="alert">{describe(saga.error)}</p><Link href="/sagas/">{t("saga.back")}</Link></div>;
  if (!saga.data || (id !== "0" && routes.isPending)) return <PageSkeleton />;

  const { saga: s, games } = saga.data;
  const emulation = Number(s.id) === 0;
  const companies = [...new Set(s.platforms.map((p) => consoles?.find((c) => c.id === p)?.company).filter(Boolean))];
  const years = games.map((g) => Number(g.year)).filter((y) => y > 0);
  const span = years.length ? (Math.min(...years) === Math.max(...years) ? `${years[0]}` : `${Math.min(...years)}–${Math.max(...years)}`) : null;

  return (
    <div className="accented" style={hero.style}>
      <section className="hero">
        {hero.art}
        <div className="hero-main">
          <span className="small muted">{[...companies, span, t("games", { count: Number(s.games) })].filter(Boolean).join(" · ")}</span>
          <h2 className="hero-title">{emulation ? t("emulation") : s.name}</h2>
          <div className="hero-actions">
            <button className="ot-btn" type="button" aria-pressed={editing} onClick={() => setEditing(!editing)}>{editing ? t("edit.done") : t("edit.edit")}</button>
            {!emulation && <PublishButton kind="saga" id={Number(s.id)} />}
            {!emulation && <MoreActions sagaId={String(s.id)} />}
          </div>
        </div>
      </section>
      <div className="content">
        {editing && !emulation && <SagaDataEditor key={`${s.name}|${s.iconic}`} sagaId={String(s.id)} name={s.name} iconic={!!s.iconic} />}
        {!emulation && <Routes sagaId={String(s.id)} editing={editing} games={more.data ? more.items : games} />}
        <section className="sec">
          <div className="sec-h"><h2>{t("saga.allGames")}</h2><span className="small muted">{t("games", { count: Number(s.games) })}</span></div>
          {editing && !emulation && <AddGame sagaId={String(s.id)} />}
          <div className="panel table-wrap" style={{ padding: 0 }}>
            <table className="t">
              <thead><tr><th>{t("saga.game")}</th><th>{t("saga.year")}</th><th>{t("saga.platforms")}</th><th>{t("saga.state")}</th>{editing && <th>{t("edit.actions")}</th>}</tr></thead>
              <tbody>
                {(more.data ? more.items : games).map((g, _, all) => (
                  <tr key={g.id} className={g.platforms.length === 0 ? "is-unplayable" : undefined}>
                    <td><Link href={`/game/?id=${g.id}`}>{g.name}</Link></td>
                    <td>{g.year ?? "—"}</td>
                    <td><span className="chips" style={{ gap: 4, flexWrap: "nowrap" }}><ShortPlatforms ids={g.platforms} /></span></td>
                    <td>
                      <span className="chips" style={{ gap: 4 }}>
                        {g.platforms.length === 0 && <span className="ot-badge">{t("noConsole")}</span>}
                      </span>
                    </td>
                    {editing && <td><GameActions game={g} sagaId={String(s.id)} games={all} /></td>}
                  </tr>
                ))}
                {(more.data ? more.hasNextPage : Number(s.games) > games.length) && <Rows cols={editing ? 5 : 4} count={4} />}
              </tbody>
            </table>
            <div ref={more.end} aria-hidden="true" />
          </div>
        </section>
      </div>
    </div>
  );
}

/** «⋯» del encabezado (#170): por ahora, pedir a Claude que recalcule la ruta. */
function MoreActions({ sagaId }: { sagaId: string }) {
  const t = useTranslations("catalog");
  const describe = useDescribeError();
  const [open, setOpen] = useState(false);
  const recalculate = useMutation({ mutationFn: () => request<number>(`/api/sagas/${sagaId}/recalculate`, { method: "POST" }) });
  return (
    <div className="tabs-more">
      <button className="ot-btn ot-btn-icon" type="button" aria-label={t("saga.moreActions")} aria-haspopup="menu" aria-expanded={open}
        onClick={() => setOpen(!open)} onKeyDown={(e) => { if (e.key === "Escape") setOpen(false); }}>⋯</button>
      {open && (
        <>
          <div className="tabs-more-scrim" onClick={() => setOpen(false)} />
          <div className="tabs-more-menu" role="menu" onKeyDown={(e) => { if (e.key === "Escape") setOpen(false); }}>
            <button type="button" role="menuitem" className="tabs-more-item" autoFocus disabled={recalculate.isPending || recalculate.isSuccess}
              onClick={() => { setOpen(false); recalculate.mutate(); }}>{t("saga.recalculate")}</button>
          </div>
        </>
      )}
      {recalculate.isSuccess && <span className="small muted" role="status">{t("saga.queued")}</span>}
      {recalculate.error && <span className="error small" role="alert">{describe(recalculate.error)}</span>}
    </div>
  );
}

const DONE = ["finished", "completed"];

/** La ruta elegida (§3.2, #170): pestañas junto al título con el avance al lado, los pasos unidos por una línea y el porqué con fuentes. */
function Routes({ sagaId, editing, games }: { sagaId: string; editing: boolean; games: GameCard[] }) {
  const t = useTranslations("catalog");
  const describe = useDescribeError();
  const consoleName = useConsoleName();
  const routes = useQuery(routesQuery(sagaId));
  const [picked, setPicked] = useState<string | null>(null);
  const [editor, setEditor] = useState<RouteKind | null>(null);

  if (!routes.data) return routes.isError ? <p className="error" role="alert">{describe(routes.error)}</p> : null;
  const list = routes.data.routes;
  const chosen = list.find((r) => String(r.id) === picked) ?? list.find((r) => r.kind === "community") ?? list[0];
  // Editar (#17): un botón por tipo de ruta, para cambiar la que hay o crear la que falta.
  const editBar = editing && (
    <div className="chips">
      {ROUTE_KINDS.map((k) => (
        <button key={k} className="chip" type="button" aria-pressed={editor === k} onClick={() => setEditor(editor === k ? null : k)}>
          {t(list.some((r) => r.kind === k) ? "edit.routeEdit" : "edit.routeCreate", { kind: t(`route.${k}`) })}
        </button>
      ))}
    </div>
  );
  const routeEditor = editing && editor && (
    <RouteEditor key={editor} sagaId={sagaId} kind={editor} route={list.find((r) => r.kind === editor)} games={games} onDone={() => setEditor(null)} />
  );
  if (!chosen) return <section className="sec"><div className="sec-h"><h2>{t("saga.route")}</h2></div>{editBar}{routeEditor || <p className="small muted">{t("saga.noRoutes")}</p>}</section>;

  return (
    <section className="sec">
      <div className="sec-h route-head">
        <h2>{t("saga.route")}</h2>
        <div className="route-tabs">
          <Tabs tabs={list.map((r) => ({ id: String(r.id), label: t(`route.${r.kind as "release"}`) }))} current={String(chosen.id)}
            onSelect={(rid) => setPicked(rid)} label={t("saga.route")} panel="route-panel" />
        </div>
      </div>
      {editBar}
      {routeEditor}
      <div id="route-panel" role="tabpanel" aria-labelledby={`tab-${chosen.id}`} className="sec">
        <ol className="timeline">
          {chosen.steps.map((s) => {
            return (
              <li key={s.position}>
                <Link href={`/game/?id=${s.gameId}`} className="step-link">
                  <span className="step-cover">
                    <Cover gameId={s.gameId} name={s.name} hasCover={s.hasCover} platform={s.versionPlatform} />
                    <span className="step-num" aria-hidden="true">{Number(s.position)}</span>
                  </span>
                  <p className="ot-game-name" title={s.name}><span className="sr-only">{Number(s.position)}. </span>{s.name}</p>
                </Link>
                {s.versionPlatform && <p className="small" style={{ margin: "4px 0 0" }}>{t("saga.recommends", { platform: consoleName(s.versionPlatform) })}</p>}
                {s.reason && <p className="small muted" style={{ margin: "2px 0 0" }}>{s.reason}</p>}
              </li>
            );
          })}
        </ol>
        <div className="panel" style={{ gap: 4 }}>
          <b>{t("saga.why")}</b>
          <p className="small" style={{ margin: 0 }}>{chosen.rationale}</p>
          {chosen.sources.length > 0 && (
            <p className="small muted" style={{ margin: 0 }}>{t("saga.sources")}: {chosen.sources.map((src, i) => <span key={src.url}>{i > 0 && " · "}<a href={src.url} target="_blank" rel="noreferrer">{src.title}</a></span>)}</p>
          )}
        </div>
      </div>
    </section>
  );
}
