"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { useConsoleName } from "@/components/Catalog";
import { RecipeAdd } from "@/components/RecipeAdd";
import { RecipeCard } from "@/components/RecipeCard";
import { RecipeEditor } from "@/components/RecipeEditor";
import { Tabs } from "@/components/Tabs";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeStatus, RecipeText } from "@/lib/types";

type Recipe = Record<string, unknown>;

const KINDS = ["emulator", "port", "complement", "tool"] as const;

/**
 * Emuladores (#95, #108): una pestaña por tipo de receta (sin Sistema, decisión 364) con sus tarjetas agrupadas por consola, «Agregar
 * una receta» (RecipeAdd, #110) y el editor por secciones (RecipeEditor, #111). Se guardan en la base local, como las
 * sagas, y Publicar las lleva al PR. Al guardar, el servidor revisa el esquema y que la fuente tenga una release con un archivo que calce.
 */
export default function Emulators() {
  const t = useTranslations("emulators");
  const describe = useDescribeError();
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const recipes = useQuery({ queryKey: ["recipes"], queryFn: () => request<RecipeInfo[]>("/api/recipes") });
  // Aparte de la lista: consulta cada repo (con caché en el servidor) y main, así que llega después.
  const status = useQuery({ queryKey: ["recipes", "status"], queryFn: () => request<RecipeStatus[]>("/api/recipes/status") });
  const statusOf = new Map((status.data ?? []).map((x) => [x.id, x]));
  const [kind, setKind] = useState<(typeof KINDS)[number]>("emulator");
  const [editing, setEditing] = useState<{ recipe: Recipe; create: boolean } | null>(null);
  const [adding, setAdding] = useState(false);
  // Buscar y filtrar (#153): lo que las tareas dejaron sin publicar o con un aviso.
  const [query, setQuery] = useState("");
  const [only, setOnly] = useState<"unpublished" | "notices" | null>(null);
  const importMain = useMutation({ mutationFn: () => request<number>("/api/recipes/import", { method: "POST" }), onSuccess: () => cache.invalidateQueries({ queryKey: ["recipes"] }) });
  const open = useMutation({
    mutationFn: (id: string) => request<RecipeText>(`/api/recipes/${encodeURIComponent(id)}`),
    onSuccess: (r) => setEditing({ recipe: JSON.parse(r.json), create: false }),
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

  // Las de la pestaña, por su primera consola (la tarjeta nombra las demás); las sin consola van al final.
  const byId = new Map((recipes.data ?? []).map((r) => [r.id, r]));
  const groups = new Map<string, RecipeInfo[]>();
  const text = query.trim().toLowerCase();
  const shown = (recipes.data ?? []).filter((r) => r.kind === kind
    && (!text || [r.id, r.name, r.game, r.url].some((v) => v?.toLowerCase().includes(text)))
    && (only !== "unpublished" || r.local || statusOf.get(r.id)?.state === "changed")
    && (only !== "notices" || (r.notices?.length ?? 0) > 0));
  for (const r of shown) groups.set(r.consoles[0] ?? "", [...(groups.get(r.consoles[0] ?? "") ?? []), r]);
  const title = (key: string) => key ? consoleName(key) : t("noConsole");
  const sorted = [...groups.entries()].sort(([a], [b]) => (!a ? 1 : !b ? -1 : title(a).localeCompare(title(b))));

  return (
    <div className="content">
      <div className="toolbar">
        <button className="ot-btn ot-btn-primary" type="button" disabled={adding} onClick={() => setAdding(true)}>{t("fromRepo")}</button>
        <span className="small muted tabular toolbar-total" aria-live="polite">{recipes.data ? t("total", { count: recipes.data.length }) : ""}</span>
      </div>
      {adding && <RecipeAdd onDraft={(recipe) => { setAdding(false); setEditing({ recipe, create: true }); }} onCancel={() => setAdding(false)} />}
      {[open.error, duplicate.error, recipes.error, importMain.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert">{describe(e)}</p>)}
      {editing && <RecipeEditor key={String(editing.recipe.id)} recipe={editing.recipe} create={editing.create} onDone={() => setEditing(null)} />}
      {recipes.data?.length === 0 && (
        <section className="panel">
          <p className="muted" style={{ margin: 0 }}>{t("empty")}</p>
          <div><button className="ot-btn" type="button" disabled={importMain.isPending} onClick={() => importMain.mutate()}>{t("importMain")}</button></div>
        </section>
      )}
      <Tabs tabs={KINDS.map((k) => ({ id: k, label: t(`tabs.${k}`) }))} current={kind} onSelect={(id) => setKind(id as typeof kind)} label={t("tabsLabel")} panel="recipes-panel" />
      <div className="chips" style={{ alignItems: "center" }}>
        <input type="search" aria-label={t("search")} placeholder={t("search")} value={query} onChange={(e) => setQuery(e.target.value)} />
        {(["unpublished", "notices"] as const).map((k) => (
          <button key={k} className="chip" type="button" aria-pressed={only === k} onClick={() => setOnly(only === k ? null : k)}>{t(`filter.${k}`)}</button>
        ))}
      </div>
      <div id="recipes-panel" role="tabpanel" aria-labelledby={`tab-${kind}`} style={{ display: "grid", gap: 8 }}>
        {recipes.data && recipes.data.length > 0 && sorted.length === 0 && <p className="muted">{t("emptyTab")}</p>}
        {sorted.map(([key, list]) => (
          <div key={key} style={{ display: "grid", gap: 8 }}>
            <h2 className="console-group">{title(key)}</h2>
            {list.map((r) => (
              <RecipeCard key={r.id} recipe={r} original={byId.get(String(r.forkOf))} forks={recipes.data?.filter((f) => f.forkOf === r.id)} status={statusOf.get(r.id)} editing={editing?.recipe.id === r.id} busy={open.isPending || duplicate.isPending} onEdit={() => open.mutate(r.id)} onDuplicate={() => duplicate.mutate(r.id)} />
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}
