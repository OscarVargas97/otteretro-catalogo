"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";
import { Empty } from "@/components/Shell";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { Panels } from "@/components/Skeleton";
import { reviewQuery, sagaQuery } from "@/lib/queries";
import { Cover, useConsoleName } from "@/components/Catalog";
import { RecipeAdd } from "@/components/RecipeAdd";
import { RecipeEditor } from "@/components/RecipeEditor";
import type { EmulatorPick, FileReview, ReviewItem, ReviewPage, SearchResults } from "@/lib/types";

// Lo que trae cada resultado de Claude (F2.5): la forma depende del prompt.
type Source = { title: string; url: string };
type Route = { kind: "release" | "story" | "community"; rationale: string; steps: { gameId: number; versionId: number | null; reason: string }[]; sources: Source[] };
type Iconic = { iconic: boolean; gameIds: number[]; reason: string; sources: Source[] };
type Criterion = { score: number; text: string };
type Best = { bestVersionId: number; reason: string; versions: { versionId: number; content: Criterion; gameplay: Criterion; technical: Criterion; original: Criterion }[] };
type Kind = { kind: string; reason: string; before: string };
type Text = { text: string };

/** Una fila de la bandeja: lo de Claude por aprobar (type = su prompt), una recomendación de emulador (picks) o un archivo (doubtful, unidentified). */
type Entry = { id: number; type: string; title: string; item?: ReviewItem; pick?: EmulatorPick; file?: FileReview };
type Call = { path: string; body?: unknown };
type Done = { ids: number[]; text: string; call: Call };

// Lo aprobado o rechazado espera este rato antes de ir al servidor, para poder deshacerlo sin tocar el catálogo.
const UNDO_MS = 8000;
const FILE_TYPES = ["doubtful", "unidentified"];

/**
 * Revisar como bandeja (#164): lista corta a la izquierda y el detalle de uno a la derecha. Aprobar (A), Rechazar (R) y Saltar (S, ↑↓)
 * con el teclado; lo hecho se puede deshacer hasta que se manda. Los archivos dudosos o sin identificar (#181) quedan como filtro,
 * hasta confirmar si se revisan solo en Otteretro.
 */
export default function Review() {
  const t = useTranslations("review");
  const describe = useDescribeError();
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const page = useQuery(reviewQuery);
  const [type, setType] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [hidden, setHidden] = useState<number[]>([]);
  const [last, setLast] = useState<Done | null>(null);
  const [routes, setRoutes] = useState<Route[] | null>(null);
  const queued = useRef<Done | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const act = useMutation({
    mutationFn: ({ path, body }: Call) =>
      request<ReviewPage>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) }),
    onSuccess: (data) => {
      cache.setQueryData(["review"], data);
      for (const key of ["learnings", "recipes", "saga", "home", "notices", "collection", "consoles", "game"]) cache.invalidateQueries({ queryKey: [key] });
      cache.invalidateQueries({ queryKey: ["review", "count"] });
    },
  });

  // Manda lo que esperaba; si falla, vuelve a la lista.
  const flush = () => {
    const done = queued.current;
    queued.current = null;
    setLast(null);
    if (done) act.mutateAsync(done.call).catch(() => setHidden((h) => h.filter((id) => !done.ids.includes(id))));
  };
  const flushRef = useRef(flush);
  flushRef.current = flush;
  useEffect(() => {
    if (!last) return;
    const timer = setTimeout(() => flushRef.current(), UNDO_MS);
    return () => clearTimeout(timer);
  }, [last]);
  useEffect(() => () => flushRef.current(), []); // al salir de la pantalla, lo pendiente se manda

  const data = page.data;
  const entries: Entry[] = data ? [
    ...data.pending.map((i) => ({ id: Number(i.id), type: i.prompt, title: i.title, item: i })),
    ...data.picks.map((p) => ({ id: Number(p.id), type: "picks", title: p.recipe ? p.recipeName || p.recipe : t("pick.noRecipe", { repo: p.repo ?? "" }), pick: p })),
    ...data.files.map((f) => ({ id: Number(f.id), type: f.kind, title: f.path.split(/[\\/]/).pop() ?? f.path, file: f })),
  ].filter((e) => !hidden.includes(e.id)) : [];
  const shown = type && entries.some((e) => e.type === type) ? type : null;
  const list = entries.filter((e) => !shown || e.type === shown);
  const at = Math.max(0, list.findIndex((e) => e.id === selected));
  const current = list[at] as Entry | undefined;
  const near = (step: number) => (list.length > 1 ? list[(at + step + list.length) % list.length].id : null);
  const label = (k: string) => t(FILE_TYPES.includes(k) || k === "picks" ? `type.${k as "picks"}` : `prompt.${k as "ruta-saga"}`);

  useEffect(() => {
    listRef.current?.querySelector('[aria-current="true"]')?.scrollIntoView({ block: "nearest" });
  }, [current?.id]);

  const select = (id: number | null) => { setSelected(id); setRoutes(null); };
  const run = (ids: number[], text: string, call: Call) => {
    flush();
    queued.current = { ids, text, call };
    setHidden((h) => [...h, ...ids]);
    setLast(queued.current);
  };
  const undo = () => {
    const done = queued.current;
    if (!done) return;
    queued.current = null;
    setLast(null);
    setHidden((h) => h.filter((id) => !done.ids.includes(id)));
    select(done.ids[0]);
  };
  const close = (call: Call | null, verb: "approved" | "rejected" | "resolved") => {
    if (!current || !call) return;
    run([current.id], t(`done.${verb}`, { title: current.title }), call);
    select(near(1));
  };
  const approveCall = (e: Entry): Call | null =>
    e.item ? (routes ? { path: `/api/review/${e.id}/approve-edited`, body: { routes } } : { path: `/api/review/${e.id}/approve` })
      : e.pick ? (e.pick.recipe ? { path: `/api/review/${e.id}/pick/accept` } : null)
        : e.file?.kind === "doubtful" ? { path: `/api/review/${e.id}/file`, body: { gameId: null } } : null;
  const rejectCall = (e: Entry): Call | null =>
    e.item ? { path: `/api/review/${e.id}/reject` } : e.pick ? { path: `/api/review/${e.id}/dismiss` } : null;
  const approve = () => current && close(approveCall(current), "approved");
  const reject = () => current && close(rejectCall(current), "rejected");

  // Teclas de la bandeja; no cuando se escribe en un campo.
  const keys = useRef<(e: KeyboardEvent) => void>(() => {});
  keys.current = (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || !current) return;
    if ((e.target as HTMLElement | null)?.closest?.("input, textarea, select, [contenteditable]")) return;
    const go = { a: approve, r: reject, s: () => select(near(1)), arrowdown: () => select(near(1)), arrowup: () => select(near(-1)) }[e.key.toLowerCase()];
    if (!go) return;
    e.preventDefault();
    go();
  };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => keys.current(e);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  if (page.isError) return <div className="content"><p className="error" role="alert">{describe(page.error)}</p></div>;
  if (!data) return <div className="content" aria-busy="true"><Panels count={3} /></div>;

  // «Aprobar las N» (reemplaza «Aceptar todo», #153): lo de Claude de un tipo, o los emuladores con receta. Los archivos, uno a uno.
  const batch = shown && !FILE_TYPES.includes(shown) ? list.filter((e) => !e.pick || e.pick.recipe) : [];
  const approveBatch = () => {
    run(batch.map((e) => e.id), t("done.approvedType", { count: batch.length, type: label(shown!) }),
      { path: shown === "picks" ? "/api/review/accept-all?tab=picks" : `/api/review/approve-all?prompt=${encodeURIComponent(shown!)}` });
    setType(null);
    select(null);
  };
  const types = [...new Set(entries.map((e) => e.type))];

  return (
    <div className="content">
      <p className="small muted" style={{ margin: 0 }}>{t("intro")}</p>
      {entries.length > 0 && <div style={{ display: "flex", flexWrap: "wrap", gap: 12, alignItems: "center", justifyContent: "space-between" }}>
        <div className="chips" role="group" aria-label={t("filterType")}>
          {[null, ...types].map((k) => (
            <button key={k ?? "all"} className="chip" type="button" aria-pressed={shown === k} onClick={() => { setType(k); select(null); }}>
              {k ? label(k) : t("all")} <span className="muted">{k ? entries.filter((e) => e.type === k).length : entries.length}</span>
            </button>
          ))}
        </div>
        {batch.length > 1 && <button className="ot-btn" type="button" onClick={approveBatch}>{t("approveType", { count: batch.length, type: label(shown!) })}</button>}
      </div>}
      {last && <div className="panel" role="status" style={{ flexDirection: "row", alignItems: "center", gap: 12, padding: "10px 14px" }}>
        <span className="small">{last.text}</span>
        <button className="ot-btn ot-btn-ghost" type="button" onClick={undo}>{t("undo")}</button>
      </div>}
      {act.error && <p className="error small" role="alert">{describe(act.error)}</p>}

      {!current ? (
        <Empty title={t("emptyTitle")} text={t("emptyText")}><Link className="ot-btn" href="/settings/?s=claude">{t("openClaude")}</Link></Empty>
      ) : (
        <div className="inbox">
          <div className="panel inbox-list" ref={listRef} role="list" aria-label={t("list")}>
            {list.map((e) => (
              <div key={e.id} role="listitem">
                <button className="inbox-row" type="button" aria-current={e.id === current.id} onClick={() => select(e.id)}>
                  <span style={{ display: "flex", gap: 8, alignItems: "center", minWidth: 0 }}>
                    <span className="ot-badge ot-badge-info">{label(e.type)}</span><b className="one">{e.title}</b>
                  </span>
                  <span className="small muted one">
                    {e.item ? <Summary item={e.item} /> : e.pick ? `${e.pick.game} · ${consoleName(e.pick.console)}` : consoleName(e.file!.console)}
                  </span>
                </button>
              </div>
            ))}
          </div>
          <section className="panel inbox-detail" aria-label={t("detail")}>
            <div className="sec-h">
              <h2>{current.item?.entity === "saga" ? <Link href={`/saga/?id=${current.item.entityId}`}>{current.title}</Link> : current.title}</h2>
              <span className="ot-badge ot-badge-info">{label(current.type)}</span>
            </div>
            {current.item && (routes ? <RouteEditor routes={routes} onChange={setRoutes} /> : <Result item={current.item} />)}
            {current.pick && <Pick key={current.id} pick={current.pick} />}
            {current.file && <FileItem key={current.id} file={current.file} resolve={(gameId) => close({ path: `/api/review/${current.id}/file`, body: { gameId } }, "resolved")} />}
            <div className="inbox-actions">
              <button className="ot-btn ot-btn-primary" type="button" aria-keyshortcuts="A" disabled={!approveCall(current)} onClick={approve}>
                {routes ? t("approveEdited") : t("approve")}<span className="kbd-hint" aria-hidden="true">A</span></button>
              {current.item?.prompt === "ruta-saga" && <button className="ot-btn" type="button"
                onClick={() => setRoutes(routes ? null : JSON.parse(current.item!.result) as Route[])}>{routes ? t("cancel") : t("edit")}</button>}
              <button className="ot-btn" type="button" aria-keyshortcuts="R" disabled={!rejectCall(current)} onClick={reject}>
                {t("reject")}<span className="kbd-hint" aria-hidden="true">R</span></button>
              <button className="ot-btn ot-btn-ghost" type="button" aria-keyshortcuts="S" disabled={list.length < 2} onClick={() => select(near(1))}>
                {t("skip")}<span className="kbd-hint" aria-hidden="true">S</span></button>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

/** Una línea de lo que propone, para la fila de la bandeja. */
function Summary({ item }: { item: ReviewItem }) {
  const t = useTranslations("review");
  const tc = useTranslations("catalog");
  const value: unknown = JSON.parse(item.result);
  switch (item.prompt) {
    case "ruta-saga": return (value as Route[]).map((r) => tc(`route.${r.kind}`)).join(" · ");
    case "iconico": return (value as Iconic).iconic ? t("iconicYes") : t("iconicNo");
    case "mejor-version": return (value as Best).reason;
    case "clasificar-version": {
      const v = value as Kind;
      return `${tc(`kind.${v.before as "original"}`)} → ${tc(`kind.${v.kind as "original"}`)}`;
    }
    case "traducir-descripcion": return (value as Text).text;
    default: return null;
  }
}

function RouteEditor({ routes, onChange }: { routes: Route[]; onChange: (routes: Route[]) => void }) {
  const t = useTranslations("review");
  const tc = useTranslations("catalog");
  const update = (r: number, steps: Route["steps"]) => onChange(routes.map((x, i) => (i === r ? { ...x, steps } : x)));
  const move = (r: number, from: number, to: number) => {
    const steps = [...routes[r].steps];
    steps.splice(to, 0, ...steps.splice(from, 1));
    update(r, steps);
  };
  return <>{routes.map((route, r) => (
    <div key={route.kind}>
      <b>{tc(`route.${route.kind}`)}</b>
      <ol className="small" style={{ margin: "4px 0" }}>
        {route.steps.map((s, i) => (
          <li key={s.gameId} style={{ display: "flex", gap: 6, alignItems: "center" }}>
            <span style={{ flex: 1 }}>{s.reason}</span>
            <button className="ot-btn ot-btn-sm" type="button" aria-label={t("stepUp", { step: i + 1 })} disabled={i === 0} onClick={() => move(r, i, i - 1)}>↑</button>
            <button className="ot-btn ot-btn-sm" type="button" aria-label={t("stepDown", { step: i + 1 })} disabled={i === route.steps.length - 1} onClick={() => move(r, i, i + 1)}>↓</button>
            <button className="ot-btn ot-btn-sm" type="button" aria-label={t("stepRemove", { step: i + 1 })} onClick={() => update(r, route.steps.filter((_, j) => j !== i))}>×</button>
          </li>
        ))}
      </ol>
    </div>
  ))}</>;
}

/** Las rutas por aprobar con sus portadas numeradas y la razón de cada paso (#171). Nombres y portadas salen de la saga. */
function RoutesPreview({ sagaId, routes, sources }: { sagaId: string; routes: Route[]; sources: (list: Source[]) => React.ReactNode }) {
  const tc = useTranslations("catalog");
  const saga = useQuery(sagaQuery(sagaId));
  const game = (id: number) => saga.data?.games.find((g) => Number(g.id) === id);
  return <>{routes.map((r) => (
    <div key={r.kind} className="sec">
      <p style={{ margin: 0 }}><b>{tc(`route.${r.kind}`)}</b> · <span className="small">{r.rationale}</span></p>
      <ol className="timeline is-plain">
        {r.steps.map((s, i) => {
          const g = game(s.gameId);
          return (
            <li key={i}>
              <span className="step-cover">
                <Cover gameId={s.gameId} name={g?.name ?? ""} hasCover={!!g?.hasCover} platform={g?.platforms[0]} />
                <span className="step-num" aria-hidden="true">{i + 1}</span>
              </span>
              <p className="ot-game-name" title={g?.name}><span className="sr-only">{i + 1}. </span>{g?.name ?? "…"}</p>
              <p className="small muted" style={{ margin: 0 }}>{s.reason}</p>
            </li>
          );
        })}
      </ol>
      {sources(r.sources)}
    </div>
  ))}</>;
}

/** El resultado en palabras, según el prompt. */
function Result({ item }: { item: ReviewItem }) {
  const t = useTranslations("review");
  const tc = useTranslations("catalog");
  const value: unknown = JSON.parse(item.result);
  const sources = (list: Source[]) => list.length > 0 && (
    <p className="small muted">{t("sources")}: {list.map((s, i) => <span key={s.url}>{i > 0 && " · "}<a href={s.url} target="_blank" rel="noreferrer">{s.title}</a></span>)}</p>
  );
  switch (item.prompt) {
    case "ruta-saga":
      return <RoutesPreview sagaId={String(item.entityId)} routes={value as Route[]} sources={sources} />;
    case "iconico": {
      const v = value as Iconic;
      return <><p>{v.iconic ? t("iconicYes") : t("iconicNo")} · {v.reason}</p>{sources(v.sources)}</>;
    }
    case "mejor-version": {
      const v = value as Best;
      return <p>{t("bestVersion", { count: v.versions.length })} · {v.reason}</p>;
    }
    case "clasificar-version": {
      const v = value as Kind;
      return <p>{tc(`kind.${v.before as "original"}`)} → <b>{tc(`kind.${v.kind as "original"}`)}</b> · {v.reason}</p>;
    }
    case "traducir-descripcion":
      return <p className="small">{(value as Text).text}</p>;
    default:
      return null;
  }
}

/**
 * Una recomendación de «Mejor emulador» (#131): receta, juego, consola, fuente y motivo. Sin receta, «Agregar la receta» abre «Agregar una
 * receta» (decisión 367) con el repo puesto; al guardarla, la recomendación la encuentra por su repo y ya se puede aprobar.
 */
function Pick({ pick }: { pick: EmulatorPick }) {
  const t = useTranslations("review");
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const [draft, setDraft] = useState<Record<string, unknown> | null>(null);
  const [adding, setAdding] = useState(false);
  return (
    <>
      <span className="small"><Link href={`/game/?id=${pick.gameId}`}>{pick.game}</Link> · {consoleName(pick.console)}</span>
      <span className="small">{pick.reason}</span>
      <span className="small muted">{t("pick.source")}: <a href={pick.source} target="_blank" rel="noreferrer">{pick.source}</a></span>
      <span className="small muted">{pick.recipe ? t("pick.acceptHint") : t("pick.addHint")}</span>
      <div className="hero-actions">
        {!pick.recipe && <button className="ot-btn" type="button" disabled={adding || !!draft} onClick={() => setAdding(true)}>{t("pick.addRecipe")}</button>}
        {pick.repo && <a className="ot-btn" href={pick.repo} target="_blank" rel="noreferrer">{t("pick.openRepo")}</a>}
      </div>
      {adding && <RecipeAdd repo={pick.repo ?? ""} onDraft={(r) => { setAdding(false); setDraft(r); }} onCancel={() => setAdding(false)} />}
      {draft && <RecipeEditor recipe={draft} create onDone={() => { setDraft(null); cache.invalidateQueries({ queryKey: ["review"] }); }} />}
    </>
  );
}

/** Un archivo dudoso o sin identificar (#181): qué es, qué propuso Claude y el buscador para elegir otro juego. Aprobar confirma lo propuesto. */
function FileItem({ file, resolve }: { file: FileReview; resolve: (gameId: number) => void }) {
  const t = useTranslations("review");
  const consoleName = useConsoleName();
  const [picking, setPicking] = useState(file.kind === "unidentified");
  const [query, setQuery] = useState("");
  const text = query.trim();
  const search = useQuery({
    queryKey: ["search", text],
    queryFn: () => request<SearchResults>(`/api/search?${new URLSearchParams({ q: text })}`),
    enabled: picking && text.length >= 2,
  });
  // Solo los juegos que salen en la consola del archivo.
  const games = (search.data?.games ?? []).filter((g) => g.platforms.includes(file.console)).slice(0, 8);
  return (
    <>
      <div><span className="folder-path">{file.path}</span>{file.component && <span className="ot-badge" style={{ marginLeft: 6 }}>{file.component}</span>}</div>
      <span className="small muted">{consoleName(file.console)}{file.titleId ? ` · ${file.titleId}` : ""}</span>
      {file.proposed
        ? <span className="small">{t("files.proposed", { name: file.proposed })}{file.reason && <span className="muted"> · {file.reason}</span>}</span>
        : file.reason && <span className="small muted">{t("files.noProposal", { reason: file.reason })}</span>}
      {file.gameId != null && <span className="small">{t("files.assigned")} <Link href={`/game/?id=${file.gameId}`}>{file.gameName}</Link></span>}
      {!picking && <div className="hero-actions"><button className="ot-btn ot-btn-sm" type="button" onClick={() => setPicking(true)}>{t("files.change")}</button></div>}
      {picking && (
        <div style={{ display: "grid", gap: 6 }}>
          <input type="search" aria-label={t("files.search")} placeholder={t("files.search")} value={query} onChange={(e) => setQuery(e.target.value)} />
          {games.map((g) => (
            <button key={g.id} className="ot-btn ot-btn-sm" type="button" style={{ justifySelf: "start" }} onClick={() => resolve(Number(g.id))}>
              {g.name}{g.year ? ` (${g.year})` : ""}
            </button>
          ))}
          {search.data && games.length === 0 && <span className="small muted">{t("files.noResults", { console: consoleName(file.console) })}</span>}
        </div>
      )}
    </>
  );
}
