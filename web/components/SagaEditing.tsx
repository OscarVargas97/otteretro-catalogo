"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { useDeferredValue, useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { gameQuery } from "@/lib/queries";
import type { GameCard, NewSagaResponse, RouteEdit, RouteInfo, RoutesPage, SearchResults } from "@/lib/types";
import { useConsoleName } from "./Catalog";

/** Editar el catálogo a mano (#17). Lo que se cambia acá ni IGDB ni Claude lo pisan, y se publica con su saga. */

export const ROUTE_KINDS = ["release", "story", "community"] as const;
export type RouteKind = (typeof ROUTE_KINDS)[number];

const json = (method: string, body?: unknown) => ({ method, body: body === undefined ? undefined : JSON.stringify(body) });

/** Después de un cambio: la saga, sus juegos, sus rutas y la lista de Sagas se vuelven a pedir. */
function useRefresh() {
  const cache = useQueryClient();
  return (sagaId?: string) => {
    if (sagaId) cache.invalidateQueries({ queryKey: ["saga", sagaId] });
    cache.invalidateQueries({ queryKey: ["sagas"] });
  };
}

function useSearch(text: string) {
  const query = useDeferredValue(text.trim());
  return useQuery({
    queryKey: ["search", query],
    queryFn: () => request<SearchResults>(`/api/search?q=${encodeURIComponent(query)}`),
    enabled: query.length >= 2,
  });
}

function ErrorLine({ error }: { error: unknown }) {
  const describe = useDescribeError();
  return error ? <p className="error small" role="alert" style={{ margin: 0 }}>{describe(error)}</p> : null;
}

/** Nombre e icónica de la saga. */
export function SagaDataEditor({ sagaId, name, iconic }: { sagaId: string; name: string; iconic: boolean }) {
  const t = useTranslations("catalog.edit");
  const refresh = useRefresh();
  const [text, setText] = useState(name);
  const rename = useMutation({ mutationFn: () => request(`/api/sagas/${sagaId}/name`, json("PUT", { value: text })), onSuccess: () => refresh(sagaId) });
  const mark = useMutation({ mutationFn: (value: boolean) => request(`/api/sagas/${sagaId}/iconic`, json("PUT", { iconic: value })), onSuccess: () => refresh(sagaId) });
  return (
    <section className="panel">
      <form className="sentence" onSubmit={(e) => { e.preventDefault(); rename.mutate(); }}>
        <input style={{ flex: 1 }} aria-label={t("name")} value={text} onChange={(e) => setText(e.target.value)} />
        <button className="ot-btn" type="submit" disabled={rename.isPending || !text.trim() || text.trim() === name}>{t("save")}</button>
      </form>
      <label className="sentence">
        <input type="checkbox" role="switch" checked={iconic} disabled={mark.isPending} onChange={(e) => mark.mutate(e.target.checked)} />
        {t("iconic")}
      </label>
      <p className="small muted" style={{ margin: 0 }}>{t("manualNote")}</p>
      <ErrorLine error={rename.error ?? mark.error} />
    </section>
  );
}

type Step = { gameId: number; versionId: number | null; reason: string };

/** Crear o cambiar una ruta: sus pasos en orden, con la versión que se recomienda y el porqué. */
export function RouteEditor({ sagaId, kind, route, games, onDone }: { sagaId: string; kind: RouteKind; route?: RouteInfo; games: GameCard[]; onDone: () => void }) {
  const t = useTranslations("catalog.edit");
  const tc = useTranslations("catalog");
  const cache = useQueryClient();
  const [rationale, setRationale] = useState(route?.rationale ?? "");
  const [steps, setSteps] = useState<Step[]>(route?.steps.map((s) => ({ gameId: Number(s.gameId), versionId: s.versionId == null ? null : Number(s.versionId), reason: s.reason ?? "" })) ?? []);
  const [confirming, setConfirming] = useState(false);
  // Los juegos de la saga y los que la ruta ya tiene aunque estén en otra.
  const options = new Map<number, string>(games.map((g) => [Number(g.id), g.name]));
  route?.steps.forEach((s) => options.has(Number(s.gameId)) || options.set(Number(s.gameId), s.name));
  const done = (page: RoutesPage) => { cache.setQueryData(["saga", sagaId, "routes"], page); onDone(); };
  const save = useMutation({
    mutationFn: () => request<RoutesPage>(`/api/sagas/${sagaId}/routes/${kind}`, json("PUT", { rationale, steps: steps.map((s) => ({ ...s, reason: s.reason || null })) } satisfies RouteEdit)),
    onSuccess: done,
  });
  const remove = useMutation({ mutationFn: () => request<RoutesPage>(`/api/sagas/${sagaId}/routes/${kind}`, json("DELETE")), onSuccess: done });
  const change = (i: number, patch: Partial<Step>) => setSteps(steps.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const swap = (i: number, j: number) => { const next = [...steps]; [next[i], next[j]] = [next[j], next[i]]; setSteps(next); };
  const first = [...options.keys()][0];

  return (
    <section className="panel route-editor">
      <h3 style={{ margin: 0 }}>{tc(`route.${kind}`)}</h3>
      <label className="field">
        <span className="small muted">{t("rationale")}</span>
        <textarea rows={2} value={rationale} onChange={(e) => setRationale(e.target.value)} />
      </label>
      <ol className="edit-steps">
        {steps.map((s, i) => (
          <li key={i} className="sentence" style={{ flexWrap: "wrap" }}>
            <span className="small muted">{t("step", { n: i + 1 })}</span>
            <select className="ot-btn" aria-label={t("game")} value={s.gameId} onChange={(e) => change(i, { gameId: Number(e.target.value), versionId: null })}>
              {[...options].map(([id, name]) => <option key={id} value={id}>{name}</option>)}
            </select>
            <StepVersion gameId={s.gameId} value={s.versionId} onChange={(versionId) => change(i, { versionId })} />
            <input style={{ flex: 1, minWidth: 160 }} aria-label={t("reason")} placeholder={t("reason")} value={s.reason} onChange={(e) => change(i, { reason: e.target.value })} />
            <button className="ot-btn ot-btn-icon" type="button" aria-label={t("up")} disabled={i === 0} onClick={() => swap(i, i - 1)}>↑</button>
            <button className="ot-btn ot-btn-icon" type="button" aria-label={t("down")} disabled={i === steps.length - 1} onClick={() => swap(i, i + 1)}>↓</button>
            <button className="ot-btn ot-btn-icon" type="button" aria-label={t("removeStep")} onClick={() => setSteps(steps.filter((_, j) => j !== i))}>✕</button>
          </li>
        ))}
      </ol>
      <div className="sentence" style={{ flexWrap: "wrap" }}>
        <button className="ot-btn" type="button" disabled={first === undefined} onClick={() => setSteps([...steps, { gameId: first!, versionId: null, reason: "" }])}>{t("addStep")}</button>
        <span style={{ flex: 1 }} />
        {route && (confirming
          ? <><span className="small muted">{t("routeDeleteConfirm")}</span><button className="ot-btn" type="button" disabled={remove.isPending} onClick={() => remove.mutate()}>{t("confirm")}</button></>
          : <button className="ot-btn ot-btn-ghost" type="button" onClick={() => setConfirming(true)}>{t("routeDelete")}</button>)}
        <button className="ot-btn ot-btn-ghost" type="button" onClick={onDone}>{t("cancel")}</button>
        <button className="ot-btn ot-btn-primary" type="button" disabled={save.isPending || steps.length === 0} onClick={() => save.mutate()}>{t("save")}</button>
      </div>
      <ErrorLine error={save.error ?? remove.error} />
    </section>
  );
}

function StepVersion({ gameId, value, onChange }: { gameId: number; value: number | null; onChange: (id: number | null) => void }) {
  const t = useTranslations("catalog.edit");
  const consoleName = useConsoleName();
  const game = useQuery(gameQuery(String(gameId)));
  return (
    <select className="ot-btn" aria-label={t("version")} value={value ?? ""} onChange={(e) => onChange(e.target.value === "" ? null : Number(e.target.value))}>
      <option value="">{t("noVersion")}</option>
      {game.data?.versions.map((v) => <option key={v.id} value={v.id}>{[consoleName(v.platform), v.kind, v.year].filter(Boolean).join(" · ")}</option>)}
    </select>
  );
}

/** Lo que se puede hacer con un juego de la saga: moverlo, quitarlo, fusionarlo o eliminarlo. */
export function GameActions({ game, sagaId, games }: { game: GameCard; sagaId: string; games: GameCard[] }) {
  const t = useTranslations("catalog.edit");
  const refresh = useRefresh();
  const [mode, setMode] = useState<"" | "move" | "merge" | "delete">("");
  const [text, setText] = useState("");
  const [into, setInto] = useState("");
  const search = useSearch(mode === "move" ? text : "");
  const after = (targetSaga?: string) => { setMode(""); refresh(sagaId); if (targetSaga) refresh(targetSaga); };
  const move = useMutation({ mutationFn: (target: string | null) => request(`/api/games/${game.id}/saga`, json("PUT", { sagaId: target === null ? null : Number(target) })), onSuccess: (_, target) => after(target ?? undefined) });
  const merge = useMutation({ mutationFn: () => request(`/api/games/${game.id}/merge`, json("POST", { intoId: Number(into) })), onSuccess: () => after() });
  const remove = useMutation({ mutationFn: () => request(`/api/games/${game.id}`, json("DELETE")), onSuccess: () => after() });
  const busy = move.isPending || merge.isPending || remove.isPending;

  return (
    <div className="field-editor">
      <select className="ot-btn ot-btn-sm" aria-label={t("actions")} value={mode} disabled={busy}
        onChange={(e) => {
          const next = e.target.value;
          if (next === "takeOut") move.mutate(null);
          else setMode(next as typeof mode);
        }}>
        <option value="">{t("actions")}</option>
        <option value="move">{t("move")}</option>
        {sagaId !== "0" && <option value="takeOut">{t("takeOut")}</option>}
        {games.length > 1 && <option value="merge">{t("merge")}</option>}
        <option value="delete">{t("delete")}</option>
      </select>
      {mode === "move" && (
        <>
          <input aria-label={t("searchSaga")} placeholder={t("searchSaga")} value={text} autoFocus onChange={(e) => setText(e.target.value)} />
          {text.trim().length < 2 ? <span className="small muted">{t("typeMore")}</span>
            : search.data?.sagas.filter((s) => String(s.id) !== sagaId).map((s) => (
              <button key={s.id} className="ot-btn ot-btn-sm ot-btn-ghost" type="button" disabled={busy} onClick={() => move.mutate(String(s.id))}>{s.name}</button>
            ))}
        </>
      )}
      {mode === "merge" && (
        <span className="sentence">
          <select className="ot-btn ot-btn-sm" aria-label={t("mergeInto")} value={into} onChange={(e) => setInto(e.target.value)}>
            <option value="">{t("mergeInto")}</option>
            {games.filter((g) => g.id !== game.id).map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
          </select>
          <button className="ot-btn ot-btn-sm" type="button" disabled={busy || !into} onClick={() => merge.mutate()}>{t("confirm")}</button>
        </span>
      )}
      {mode === "merge" && <span className="small muted">{t("mergeHint")}</span>}
      {mode === "delete" && (
        <span className="sentence">
          <span className="small muted">{t("deleteConfirm")}</span>
          <button className="ot-btn ot-btn-sm" type="button" disabled={busy} onClick={() => remove.mutate()}>{t("confirm")}</button>
        </span>
      )}
      {mode !== "" && <button className="ot-btn ot-btn-sm ot-btn-ghost" type="button" onClick={() => setMode("")}>{t("cancel")}</button>}
      <ErrorLine error={move.error ?? merge.error ?? remove.error} />
    </div>
  );
}

/** Buscar un juego de otra saga (o sin saga) y traerlo a esta. */
export function AddGame({ sagaId }: { sagaId: string }) {
  const t = useTranslations("catalog.edit");
  const refresh = useRefresh();
  const [text, setText] = useState("");
  const search = useSearch(text);
  const add = useMutation({
    mutationFn: (game: { id: number | string; sagaId?: number | string | null }) => request(`/api/games/${game.id}/saga`, json("PUT", { sagaId: Number(sagaId) })),
    onSuccess: (_, game) => { refresh(sagaId); refresh(String(game.sagaId ?? 0)); },
  });
  const found = search.data?.games.filter((g) => String(g.sagaId ?? 0) !== sagaId) ?? [];
  return (
    <div className="field-editor">
      <input aria-label={t("searchGame")} placeholder={t("searchGame")} value={text} onChange={(e) => setText(e.target.value)} />
      {text.trim().length >= 2 && search.data && found.length === 0 && <span className="small muted">{t("noResults")}</span>}
      <div className="chips">
        {found.map((g) => (
          <button key={g.id} className="chip" type="button" disabled={add.isPending} onClick={() => add.mutate(g)}>
            {t("addGame")}: {g.name}{g.sagaName ? ` (${g.sagaName})` : ""}
          </button>
        ))}
      </div>
      <ErrorLine error={add.error} />
    </div>
  );
}

/** «Nueva saga» en Sagas: la crea y abre su página para seguir editándola. */
export function NewSaga() {
  const t = useTranslations("catalog.edit");
  const router = useRouter();
  const refresh = useRefresh();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const create = useMutation({
    mutationFn: () => request<NewSagaResponse>("/api/sagas", json("POST", { name })),
    onSuccess: (r) => { refresh(); router.push(`/saga/?id=${r.id}&edit=1`); },
  });
  if (!open) return <button className="ot-btn" type="button" onClick={() => setOpen(true)}>{t("newSaga")}</button>;
  return (
    <form className="sentence" onSubmit={(e) => { e.preventDefault(); create.mutate(); }}>
      <input aria-label={t("newSagaName")} placeholder={t("newSagaName")} value={name} autoFocus onChange={(e) => setName(e.target.value)} />
      <button className="ot-btn ot-btn-primary" type="submit" disabled={create.isPending || !name.trim()}>{t("create")}</button>
      <button className="ot-btn ot-btn-ghost" type="button" onClick={() => setOpen(false)}>{t("cancel")}</button>
      <ErrorLine error={create.error} />
    </form>
  );
}
