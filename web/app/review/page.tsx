"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { Empty } from "@/components/Shell";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { Panels } from "@/components/Skeleton";
import { Tabs } from "@/components/Tabs";
import { usePaged } from "@/lib/paged";
import { reviewQuery, sagaQuery } from "@/lib/queries";
import { Cover, useConsoleName } from "@/components/Catalog";
import { RecipeAdd } from "@/components/RecipeAdd";
import { RecipeEditor } from "@/components/RecipeEditor";
import type { EmulatorPick, FileReview, LearningItem, LearningPage, ReviewItem, ReviewPage, SearchResults } from "@/lib/types";

// Forks, ports y avisos de recetas viven en Emuladores (#153).
const TABS = ["pending", "doubtful", "unidentified", "picks", "learnings"] as const;
type Tab = (typeof TABS)[number];

// Lo que trae cada resultado de Claude (F2.5): la forma depende del prompt.
type Source = { title: string; url: string };
type Route = { kind: "release" | "story" | "community"; rationale: string; steps: { gameId: number; versionId: number | null; reason: string }[]; sources: Source[] };
type Iconic = { iconic: boolean; gameIds: number[]; reason: string; sources: Source[] };
type Criterion = { score: number; text: string };
type Best = { bestVersionId: number; reason: string; versions: { versionId: number; content: Criterion; gameplay: Criterion; technical: Criterion; original: Criterion }[] };
type Kind = { kind: string; reason: string; before: string };
type Text = { text: string };

// La pestaña puede venir en la URL (/review/?tab=unidentified, desde el aviso de Inicio).
export default function ReviewScreen() {
  return <Suspense><Review /></Suspense>;
}

/** Revisar (§2.1, D3): lo de Claude por aprobar, los archivos dudosos o sin identificar (#181) y los aprendizajes. */
function Review() {
  const t = useTranslations("review");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const asked = useSearchParams().get("tab");
  const [tab, setTab] = useState<Tab>(TABS.includes(asked as Tab) ? (asked as Tab) : "pending");
  const page = useQuery(reviewQuery);
  const act = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: unknown }) =>
      request<ReviewPage>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) }),
    onSuccess: (data) => {
      cache.setQueryData(["review"], data);
      cache.invalidateQueries({ queryKey: ["learnings"] });
      cache.invalidateQueries({ queryKey: ["recipes"] });
      cache.invalidateQueries({ queryKey: ["review", "count"] });
      cache.invalidateQueries({ queryKey: ["saga"] });
      cache.invalidateQueries({ queryKey: ["home"] });
      cache.invalidateQueries({ queryKey: ["notices"] });
      cache.invalidateQueries({ queryKey: ["collection"] });
      cache.invalidateQueries({ queryKey: ["consoles"] });
      cache.invalidateQueries({ queryKey: ["game"] });
    },
  });

  if (page.isError) return <div className="content"><p className="error" role="alert">{describe(page.error)}</p></div>;
  if (!page.data) return <div className="content" aria-busy="true"><Panels count={3} /></div>;
  const { pending, activeLearnings, files, picks } = page.data;
  const pendingItem = (item: ReviewItem) => (
    <Pending key={item.id} item={item} busy={act.isPending}
      approve={(routes) => act.mutate(routes ? { path: `/api/review/${item.id}/approve-edited`, body: { routes } } : { path: `/api/review/${item.id}/approve` })}
      reject={() => act.mutate({ path: `/api/review/${item.id}/reject` })} />
  );
  const count = (k: Tab) => k === "pending" ? pending.length : k === "learnings" ? Number(activeLearnings) : k === "picks" ? picks.length : files.filter((f) => f.kind === k).length;

  return (
    <div className="content">
      <Tabs tabs={TABS.map((k) => ({ id: k, label: t(`tab.${k}`, { count: count(k) }) }))}
        current={tab} onSelect={(k) => setTab(k as Tab)} label={t("tabs")} panel="review-panel" />
      {act.error && <p className="error small" role="alert">{describe(act.error)}</p>}
      {/* Aceptar todo (#153): lo de Claude y las recomendaciones de emulador con receta; los archivos y aprendizajes se ven uno a uno. */}
      {pending.length + picks.length > 0 && <div className="hero-actions">
        <button className="ot-btn" type="button" disabled={act.isPending}
          onClick={() => confirm(t("acceptAllConfirm", { pending: pending.length, picks: picks.filter((p) => p.recipe).length })) && act.mutate({ path: "/api/review/accept-all" })}>
          {t("acceptAll")}</button>
      </div>}
      <div id="review-panel" role="tabpanel" aria-labelledby={`tab-${tab}`} style={{ display: "contents" }}>

      {tab === "pending" && (pending.length === 0
        ? <Empty title={t("pendingEmptyTitle")} text={t("pendingEmptyText")}><Link className="ot-btn" href="/settings/?s=claude">{t("openClaude")}</Link></Empty>
        : <>
          <div className="hero-actions">
            <button className="ot-btn ot-btn-primary" type="button" disabled={act.isPending}
              onClick={() => confirm(t("approveAllConfirm", { count: pending.length })) && act.mutate({ path: "/api/review/approve-all" })}>
              {t("approveAll", { count: pending.length })}</button>
          </div>
          {/* Lo que no es de saga (mejor versión, clasificar, traducir) va agrupado por tipo y plegado (#56). */}
          {Object.entries(Object.groupBy(pending.filter((i) => i.entity !== "saga"), (i) => i.prompt)).map(([prompt, items = []]) => (
            <details key={prompt} className="panel">
              <summary className="sec-h">
                <h2>{t(`prompt.${prompt as "ruta-saga"}`)}</h2>
                <span className="small muted">{t("groupHint", { count: items.length })}</span>
                <button className="ot-btn ot-btn-primary" type="button" disabled={act.isPending}
                  onClick={(e) => { e.preventDefault(); act.mutate({ path: `/api/review/approve-all?prompt=${encodeURIComponent(prompt)}` }); }}>
                  {t("approveAll", { count: items.length })}</button>
              </summary>
              {items.map(pendingItem)}
            </details>
          ))}
          {pending.filter((i) => i.entity === "saga").map(pendingItem)}
        </>)}

      {(tab === "doubtful" || tab === "unidentified") && (count(tab) === 0
        ? <Empty title={t(`files.${tab}EmptyTitle`)} text={t(`files.${tab}EmptyText`)}><Link className="ot-btn" href="/collection/">{t("files.openCollection")}</Link></Empty>
        : files.filter((f) => f.kind === tab).map((f) => (
          <FileItem key={f.id} file={f} busy={act.isPending} resolve={(gameId) => act.mutate({ path: `/api/review/${f.id}/file`, body: { gameId } })} />
        )))}

      {tab === "picks" && (picks.length === 0
        ? <Empty title={t("picksEmptyTitle")} text={t("picksEmptyText")} />
        : <>{picks.some((p) => p.recipe) && <div className="hero-actions">
          <button className="ot-btn ot-btn-primary" type="button" disabled={act.isPending}
            onClick={() => act.mutate({ path: "/api/review/accept-all?tab=picks" })}>{t("approveAll", { count: picks.filter((p) => p.recipe).length })}</button>
        </div>}{picks.map((p) => (
          <Pick key={p.id} pick={p} busy={act.isPending}
            accept={() => act.mutate({ path: `/api/review/${p.id}/pick/accept` })} dismiss={() => act.mutate({ path: `/api/review/${p.id}/dismiss` })} />
        ))}</>)}

      {tab === "learnings" && <Learnings busy={act.isPending} undo={(id) => act.mutate({ path: `/api/learnings/${id}/undo` })}
        onCorrected={(data) => { cache.setQueryData(["review"], data); cache.invalidateQueries({ queryKey: ["learnings"] }); }} />}
      </div>
    </div>
  );
}

/** Un resultado por aprobar. Las rutas se pueden editar antes (F2.21): subir, bajar o quitar pasos. */
function Pending({ item, busy, approve, reject }: { item: ReviewItem; busy: boolean; approve: (routes?: Route[]) => void; reject: () => void }) {
  const t = useTranslations("review");
  const [routes, setRoutes] = useState<Route[] | null>(null);
  const editable = item.prompt === "ruta-saga";
  return (
    <section className="panel">
      <div className="sec-h">
        <h2>{item.entity === "saga" ? <Link href={`/saga/?id=${item.entityId}`}>{item.title}</Link> : item.title}</h2>
        <span className="ot-badge ot-badge-info">{t(`prompt.${item.prompt as "ruta-saga"}`)}</span>
      </div>
      {routes ? <RouteEditor routes={routes} onChange={setRoutes} /> : <Result item={item} />}
      <div className="hero-actions">
        <button className="ot-btn ot-btn-primary" type="button" disabled={busy} onClick={() => approve(routes ?? undefined)}>{routes ? t("approveEdited") : t("approve")}</button>
        {editable && !routes && <button className="ot-btn" type="button" onClick={() => setRoutes(JSON.parse(item.result) as Route[])}>{t("edit")}</button>}
        {routes && <button className="ot-btn" type="button" onClick={() => setRoutes(null)}>{t("cancel")}</button>}
        <button className="ot-btn" type="button" disabled={busy} onClick={reject}>{t("reject")}</button>
      </div>
    </section>
  );
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
 * Aprendizajes (F2.12): cada cambio en claro, con filtros, agrupado por tarea; se deshace o se corrige con una nota para Claude.
 * Llega por tramos al bajar, ya filtrado por el servidor (#177).
 */
function Learnings({ busy, undo, onCorrected }: { busy: boolean; undo: (id: number) => void; onCorrected: (page: ReviewPage) => void }) {
  const t = useTranslations("review");
  const [kind, setKind] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const text = query.trim();
  const page = usePaged(["learnings", kind, text], (offset) => `/api/learnings?${new URLSearchParams({ ...(kind && { kind }), ...(text && { q: text }), offset: String(offset) })}`,
    (p: LearningPage) => ({ items: p.items, total: Number(p.total) }));
  if (page.data && page.total === 0 && !kind && !text) return <Empty title={t("learningsEmptyTitle")} text={t("learningsEmptyText")} />;
  const groups = Map.groupBy(page.items, (l: LearningItem) => (l.taskId == null ? null : Number(l.taskId)));
  return (
    <>
      <div className="chips" style={{ alignItems: "center" }}>
        {([null, "move", "merge", "rename", "alias"] as const).map((k) => (
          <button key={k ?? "all"} className="chip" type="button" aria-pressed={kind === k} onClick={() => setKind(k)}>
            {k ? t(`kindFilter.${k}`) : t("filterAll")}
          </button>
        ))}
        <input type="search" aria-label={t("search")} placeholder={t("search")} value={query} onChange={(e) => setQuery(e.target.value)} />
      </div>
      {[...groups].map(([task, items]) => (
        <section key={task ?? "none"} className="sec">
          <div className="sec-h"><h2>{task == null ? t("noTask") : t("task", { id: task })}</h2><span className="small muted">{t("count", { count: items.length })}</span></div>
          {items.map((l) => <Learning key={l.id} l={l} busy={busy} undo={undo} onCorrected={onCorrected} />)}
        </section>
      ))}
      {(!page.data || page.hasNextPage) && <Panels count={page.data ? 2 : 4} />}
      <div ref={page.end} aria-hidden="true" />
    </>
  );
}

function Learning({ l, busy, undo, onCorrected }: { l: LearningItem; busy: boolean; undo: (id: number) => void; onCorrected: (page: ReviewPage) => void }) {
  const t = useTranslations("review");
  const describe = useDescribeError();
  const [note, setNote] = useState<string | null>(null);
  const correct = useMutation({
    mutationFn: () => request<ReviewPage>(`/api/learnings/${l.id}/correct`, { method: "POST", body: JSON.stringify({ note }) }),
    onSuccess: (data) => { setNote(null); onCorrected(data); },
  });
  const list = (names: string[]) => names.map((n) => n || t("loose")).join(", ");
  const what = l.change === "alias" ? t("aliased", { text: l.nameBefore ?? "", game: l.nameAfter ?? "?" })
    : l.change === "move"
    ? t("moved", { games: list(l.games), from: list(l.from), to: l.toKind === "new" ? t("toNew", { name: l.to ?? "" }) : l.toKind === "none" ? t("toNone") : l.to ?? "" })
    : l.change === "merge" ? t("merged", { games: list(l.games), kept: l.kept ?? "" })
      : l.change === "rename" ? t("renamed", { before: l.nameBefore ?? "?", after: l.nameAfter ?? "" })
        : t(`learning.${l.change as "move"}`);
  return (
    <div className={`panel${l.undone ? " is-unplayable" : ""}`} style={{ display: "grid", gap: 6 }}>
      <div><b>{what}</b> <span className="small muted">#{l.id}</span>{l.undone && <> <span className="ot-badge">{t("undone")}</span></>}</div>
      {l.reason && <span className="small muted">{l.reason}</span>}
      {l.rules.map((r, i) => <span key={i} className="small">{t("rule", { text: r })}</span>)}
      {note !== null ? (
        <form style={{ display: "grid", gap: 6 }} onSubmit={(e) => { e.preventDefault(); correct.mutate(); }}>
          <textarea rows={2} maxLength={500} aria-label={t("correct")} placeholder={t("correctPlaceholder")} value={note} onChange={(e) => setNote(e.target.value)} />
          <div className="hero-actions">
            <button className="ot-btn ot-btn-primary" type="submit" disabled={correct.isPending || note.trim() === ""}>{t("send")}</button>
            <button className="ot-btn" type="button" onClick={() => setNote(null)}>{t("cancel")}</button>
          </div>
          {correct.error && <p className="error small" role="alert">{describe(correct.error)}</p>}
        </form>
      ) : (
        <div className="hero-actions">
          {l.sagaId != null && <Link className="ot-btn ot-btn-sm" href={`/saga/?id=${l.sagaId}`}>{t("openSaga")}</Link>}
          {!l.undone && <button className="ot-btn ot-btn-sm" type="button" disabled={busy} onClick={() => undo(Number(l.id))}>{t("undo")}</button>}
          {l.change !== "alias" && <button className="ot-btn ot-btn-sm" type="button" onClick={() => setNote("")}>{t("correct")}</button>}
        </div>
      )}
    </div>
  );
}

/**
 * Una recomendación de «Mejor emulador» (#131): receta, juego, consola, fuente y motivo. Sin receta, «Agregar la receta» abre «Agregar una
 * receta» (decisión 367) con el repo puesto; al guardarla, la recomendación la encuentra por su repo y ya se puede aceptar.
 */
function Pick({ pick, busy, accept, dismiss }: { pick: EmulatorPick; busy: boolean; accept: () => void; dismiss: () => void }) {
  const t = useTranslations("review");
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const [draft, setDraft] = useState<Record<string, unknown> | null>(null);
  const [adding, setAdding] = useState(false);
  return (
    <section className="panel" style={{ display: "grid", gap: 6 }}>
      <div className="sec-h">
        <h2>{pick.recipe ? pick.recipeName || pick.recipe : t("pick.noRecipe", { repo: pick.repo ?? "" })}</h2>
        <span className="ot-badge ot-badge-info"><Link href={`/game/?id=${pick.gameId}`}>{pick.game}</Link> · {consoleName(pick.console)}</span>
      </div>
      <span className="small">{pick.reason}</span>
      <span className="small muted">{t("pick.source")}: <a href={pick.source} target="_blank" rel="noreferrer">{pick.source}</a></span>
      <span className="small muted">{pick.recipe ? t("pick.acceptHint") : t("pick.addHint")}</span>
      <div className="hero-actions">
        {pick.recipe
          ? <button className="ot-btn ot-btn-primary" type="button" disabled={busy} onClick={accept}>{t("pick.accept")}</button>
          : <button className="ot-btn ot-btn-primary" type="button" disabled={adding || !!draft} onClick={() => setAdding(true)}>{t("pick.addRecipe")}</button>}
        <button className="ot-btn" type="button" disabled={busy} onClick={dismiss}>{t("pick.dismiss")}</button>
        {pick.repo && <a className="ot-btn" href={pick.repo} target="_blank" rel="noreferrer">{t("pick.openRepo")}</a>}
      </div>
      {adding && <RecipeAdd repo={pick.repo ?? ""} onDraft={(r) => { setAdding(false); setDraft(r); }} onCancel={() => setAdding(false)} />}
      {draft && <RecipeEditor recipe={draft} create onDone={() => { setDraft(null); cache.invalidateQueries({ queryKey: ["review"] }); }} />}
    </section>
  );
}

/** Un archivo en Dudosas o Sin identificar (#181): qué es, qué propuso Claude, confirmar o elegir el juego con el buscador. */
function FileItem({ file, busy, resolve }: { file: FileReview; busy: boolean; resolve: (gameId: number | null) => void }) {
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
    <div className="panel" style={{ display: "grid", gap: 6 }}>
      <div><span className="folder-path">{file.path}</span>{file.component && <span className="ot-badge" style={{ marginLeft: 6 }}>{file.component}</span>}</div>
      <span className="small muted">{consoleName(file.console)}{file.titleId ? ` · ${file.titleId}` : ""}</span>
      {file.proposed
        ? <span className="small">{t("files.proposed", { name: file.proposed })}{file.reason && <span className="muted"> · {file.reason}</span>}</span>
        : file.reason && <span className="small muted">{t("files.noProposal", { reason: file.reason })}</span>}
      {file.gameId != null && <span className="small">{t("files.assigned")} <Link href={`/game/?id=${file.gameId}`}>{file.gameName}</Link></span>}
      <div className="hero-actions">
        {file.kind === "doubtful" && <button className="ot-btn ot-btn-primary ot-btn-sm" type="button" disabled={busy} onClick={() => resolve(null)}>{t("files.confirm")}</button>}
        {!picking && <button className="ot-btn ot-btn-sm" type="button" onClick={() => setPicking(true)}>{t("files.change")}</button>}
      </div>
      {picking && (
        <div style={{ display: "grid", gap: 6 }}>
          <input type="search" aria-label={t("files.search")} placeholder={t("files.search")} value={query} onChange={(e) => setQuery(e.target.value)} />
          {games.map((g) => (
            <button key={g.id} className="ot-btn ot-btn-sm" type="button" style={{ justifySelf: "start" }} disabled={busy} onClick={() => resolve(Number(g.id))}>
              {g.name}{g.year ? ` (${g.year})` : ""}
            </button>
          ))}
          {search.data && games.length === 0 && <span className="small muted">{t("files.noResults", { console: consoleName(file.console) })}</span>}
        </div>
      )}
    </div>
  );
}
