"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { Fragment, useEffect, useRef, useState } from "react";
import { useConsoleName } from "@/components/Catalog";
import { KINDS, ProgramAdd, type Kind } from "@/components/ProgramAdd";
import { RecipeCard } from "@/components/RecipeCard";
import { RecipeEditor } from "@/components/RecipeEditor";
import { Tabs } from "@/components/Tabs";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeStatus, RecipeText } from "@/lib/types";

type Recipe = Record<string, unknown>;

const PAGE = 100;

/**
 * Programas (antes Emuladores, #166): una pestaña por tipo de receta con una tabla y la columna que le sirve a cada tipo (el juego de un
 * port, las consolas de un emulador, a qué se suma un complemento, para qué sirve una herramienta). El nombre abre su tarjeta (RecipeCard)
 * con editar, duplicar, probar y descartar; «Agregar» lleva a Agregar programa en 3 pasos (ProgramAdd). Publicar las lleva al PR.
 */
export default function Programs() {
  const t = useTranslations("emulators");
  const tp = useTranslations("programs");
  const describe = useDescribeError();
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const recipes = useQuery({ queryKey: ["recipes"], queryFn: () => request<RecipeInfo[]>("/api/recipes") });
  // Aparte de la lista: consulta cada repo (con caché en el servidor) y main, así que llega después.
  const status = useQuery({ queryKey: ["recipes", "status"], queryFn: () => request<RecipeStatus[]>("/api/recipes/status") });
  const statusOf = new Map((status.data ?? []).map((x) => [x.id, x]));
  const [kind, setKind] = useState<Kind>("port");
  const [editing, setEditing] = useState<{ recipe: Recipe; create: boolean } | null>(null);
  const [adding, setAdding] = useState(false);
  const [saved, setSaved] = useState<{ name: string; kind: Kind } | null>(null);
  const [opened, setOpened] = useState<string | null>(null);
  // Buscar y filtrar (#153): lo que las tareas dejaron sin publicar o con un aviso.
  const [query, setQuery] = useState("");
  const [only, setOnly] = useState<"unpublished" | "notices" | null>(null);
  const [limit, setLimit] = useState(PAGE);
  const end = useRef<HTMLDivElement>(null);
  const importMain = useMutation({ mutationFn: () => request<number>("/api/recipes/import", { method: "POST" }), onSuccess: () => cache.invalidateQueries({ queryKey: ["recipes"] }) });
  const open = useMutation({
    mutationFn: (id: string) => request<RecipeText>(`/api/recipes/${encodeURIComponent(id)}`),
    onSuccess: (r) => {
      const recipe = JSON.parse(r.json) as Recipe;
      if (KINDS.includes(recipe.kind as Kind)) setKind(recipe.kind as Kind);
      setOpened(String(recipe.id));
      setEditing({ recipe, create: false });
    },
  });

  // /emulators/?edit=<id> abre el editor de esa receta.
  useEffect(() => {
    const id = new URLSearchParams(window.location.search).get("edit");
    if (id) open.mutate(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const duplicate = useMutation({
    mutationFn: (id: string) => request<RecipeText>(`/api/recipes/${encodeURIComponent(id)}`),
    onSuccess: (r) => {
      const src = JSON.parse(r.json) as Recipe;
      setEditing({ recipe: { ...src, id: `${src.id}${t("card.copySuffix")}`, name: t("card.copyName", { name: String(src.name ?? src.id) }) }, create: true });
    },
  });

  const all = recipes.data ?? [];
  const byId = new Map(all.map((r) => [r.id, r]));
  const text = query.trim().toLowerCase();
  const nameOf = (r: RecipeInfo) => r.name || r.id;
  // Un fork va justo después de su original (ordena por el nombre de la original y después por el suyo).
  const sortKey = (r: RecipeInfo) => `${(r.forkOf && byId.get(r.forkOf) ? nameOf(byId.get(r.forkOf)!) : nameOf(r)).toLowerCase()}\u0000${r.forkOf ? 1 : 0}${nameOf(r).toLowerCase()}`;
  const unpublished = (r: RecipeInfo) => r.local || statusOf.get(r.id)?.state === "changed";
  const forWhat = (r: RecipeInfo) => r.kind === "port" ? [r.game, r.folder].filter(Boolean).join(" · ")
    : r.kind === "emulator" ? [r.consoles.map(consoleName).join(", "), r.forkOf && t("card.forkOf", { name: byId.get(r.forkOf)?.name || r.forkOf })].filter(Boolean).join(" · ")
      : r.folder;
  const shown = all.filter((r) => r.kind === kind
    && (!text || [r.id, r.name, r.game, r.url, forWhat(r)].some((v) => v?.toLowerCase().includes(text)))
    && (only !== "unpublished" || unpublished(r))
    && (only !== "notices" || (r.notices?.length ?? 0) > 0))
    .sort((a, b) => sortKey(a).localeCompare(sortKey(b)));
  const rows = shown.slice(0, limit);
  const source = (url?: string | null) => { try { return url ? new URL(url).hostname.replace(/^www\./, "") : ""; } catch { return ""; } };

  // Al bajar se cargan más: un marcador al pie de la tabla, como usePaged pero con la lista ya en memoria.
  useEffect(() => {
    const node = end.current;
    if (!node || rows.length >= shown.length) return;
    const watch = new IntersectionObserver(([e]) => { if (e.isIntersecting) setLimit((n) => n + PAGE); }, { rootMargin: "0px 0px 1500px 0px" });
    watch.observe(node);
    return () => watch.disconnect();
  }, [rows.length, shown.length]);

  if (adding) return (
    <div className="content">
      <ProgramAdd onCancel={() => setAdding(false)} onSaved={(name, k) => { setAdding(false); setKind(k); setLimit(PAGE); setSaved({ name, kind: k }); }} />
    </div>
  );

  const pick = (k: Kind) => { setKind(k); setOnly(null); setLimit(PAGE); setOpened(null); };
  return (
    <div className="content">
      <div className="toolbar">
        <p className="small muted" style={{ margin: 0, flex: 1 }}>{tp("intro")}</p>
        <button className="ot-btn ot-btn-primary" type="button" onClick={() => { setSaved(null); setAdding(true); }}>{tp("add")}</button>
      </div>
      {saved && <p className="panel small" role="status" style={{ margin: 0 }}>{tp("saved", { name: saved.name, tab: t(`tabs.${saved.kind}`) })}</p>}
      {[open.error, duplicate.error, recipes.error, importMain.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert">{describe(e)}</p>)}
      {editing && <RecipeEditor key={String(editing.recipe.id)} recipe={editing.recipe} create={editing.create} onDone={() => setEditing(null)} />}
      {recipes.data?.length === 0 && (
        <section className="panel">
          <p className="muted" style={{ margin: 0 }}>{t("empty")}</p>
          <div><button className="ot-btn" type="button" disabled={importMain.isPending} onClick={() => importMain.mutate()}>{t("importMain")}</button></div>
        </section>
      )}
      <Tabs tabs={KINDS.map((k) => ({ id: k, label: <>{t(`tabs.${k}`)} <span className="muted">{all.filter((r) => r.kind === k).length}</span></> }))}
        current={kind} onSelect={(id) => pick(id as Kind)} label={t("tabsLabel")} panel="recipes-panel" />
      <p className="small muted" style={{ margin: 0 }}>{tp(`kind.${kind}.help`)}</p>
      <div className="chips" style={{ alignItems: "center" }}>
        <input type="search" aria-label={tp(`kind.${kind}.search`)} placeholder={tp(`kind.${kind}.search`)} value={query} onChange={(e) => { setQuery(e.target.value); setLimit(PAGE); }} />
        {(["unpublished", "notices"] as const).map((k) => (
          <button key={k} className="chip" type="button" aria-pressed={only === k} onClick={() => { setOnly(only === k ? null : k); setLimit(PAGE); }}>{t(`filter.${k}`)}</button>
        ))}
      </div>
      <div id="recipes-panel" role="tabpanel" aria-labelledby={`tab-${kind}`} className="panel table-wrap" style={{ padding: 0 }}>
        {recipes.data && recipes.data.length > 0 && shown.length === 0 ? <p className="muted" style={{ padding: 12, margin: 0 }}>{t("emptyTab")}</p> : (
          <table className="t">
            <thead><tr><th>{tp("col.name")}</th><th>{tp(`kind.${kind}.column`)}</th><th>{tp("col.source")}</th><th>{tp("col.state")}</th></tr></thead>
            <tbody>
              {rows.map((r) => {
                const state = r.local ? "local" : statusOf.get(r.id)?.state;
                return (
                  <Fragment key={r.id}>
                    <tr>
                      <td><button type="button" className="link-button" aria-expanded={opened === r.id} onClick={() => setOpened(opened === r.id ? null : r.id)}><b>{nameOf(r)}</b></button></td>
                      <td>{forWhat(r)}</td>
                      <td className="small muted">{source(r.url)}</td>
                      <td>
                        <span className="sentence">
                          {state === "published" && <span className="ot-badge ot-badge-have">{t("card.published")}</span>}
                          {(state === "changed" || state === "local") && <span className="ot-badge ot-badge-info" title={t(state === "local" ? "localHint" : "card.changedHint")}>{t("card.changed")}</span>}
                          {(r.notices?.length ?? 0) > 0 && <span className="ot-badge ot-badge-danger">{t("filter.notices")}</span>}
                        </span>
                      </td>
                    </tr>
                    {opened === r.id && (
                      <tr><td colSpan={4}>
                        <RecipeCard recipe={r} original={byId.get(String(r.forkOf))} forks={all.filter((f) => f.forkOf === r.id)} status={statusOf.get(r.id)} editing={editing?.recipe.id === r.id}
                          busy={open.isPending || duplicate.isPending} onEdit={() => open.mutate(r.id)} onDuplicate={() => duplicate.mutate(r.id)} />
                      </td></tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        )}
        <div ref={end} aria-hidden="true" />
      </div>
      {shown.length > rows.length && <p className="small muted" style={{ margin: 0 }}>{tp("shown", { shown: rows.length, total: shown.length })}</p>}
    </div>
  );
}
