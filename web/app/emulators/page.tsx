"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useConsoleName } from "@/components/Catalog";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeText } from "@/lib/types";

type Recipe = Record<string, unknown>;

const KINDS = ["emulator", "port", "complement", "tool"] as const;
const SOURCES = ["github", "forgejo", "dolphin"] as const;
const VERSIONS = ["exe", "date", "fixed"] as const;
/** Campos de texto, listas (separadas por comas) y casillas del formulario; lo demás va como JSON. */
const TEXT = ["name", "folder", "detect", "parent", "game", "link", "license", "asset", "saveAs"] as const;
const LISTS = ["consoles", "baseFor", "formats", "saves", "config", "content"] as const;
const FLAGS = ["experimental", "devOnly"] as const;
const FORM = new Set<string>(["id", "kind", "version", "updates", ...TEXT, ...LISTS, ...FLAGS]);

const json = (method: string, body: unknown) => ({ method, body: JSON.stringify(body) });
const empty = (v: unknown) => v === undefined || v === "" || v === false || (Array.isArray(v) && v.length === 0);

/**
 * Emuladores (#95): las recetas por consola, «Crear desde su repo» y un formulario por campo. Se guardan en la base local, como las
 * sagas, y Publicar las lleva al PR. Al guardar, el servidor revisa el esquema y que la fuente tenga una release con un archivo que calce.
 */
export default function Emulators() {
  const t = useTranslations("emulators");
  const describe = useDescribeError();
  const consoleName = useConsoleName();
  const cache = useQueryClient();
  const recipes = useQuery({ queryKey: ["recipes"], queryFn: () => request<RecipeInfo[]>("/api/recipes") });
  const [editing, setEditing] = useState<{ recipe: Recipe; create: boolean } | null>(null);
  const [url, setUrl] = useState("");
  const importMain = useMutation({ mutationFn: () => request<number>("/api/recipes/import", { method: "POST" }), onSuccess: () => cache.invalidateQueries({ queryKey: ["recipes"] }) });
  const draft = useMutation({
    mutationFn: () => request<RecipeText>("/api/recipes/draft", json("POST", { url })),
    onSuccess: (r) => setEditing({ recipe: JSON.parse(r.json), create: true }),
  });
  const open = useMutation({
    mutationFn: (id: string) => request<RecipeText>(`/api/recipes/${encodeURIComponent(id)}`),
    onSuccess: (r) => setEditing({ recipe: JSON.parse(r.json), create: false }),
  });

  // Por consola; una receta sin consolas (complemento, herramienta) va con las de su tipo.
  const groups = new Map<string, RecipeInfo[]>();
  for (const r of recipes.data ?? [])
    for (const key of r.consoles.length ? r.consoles.map((c) => `console:${c}`) : [`kind:${r.kind}`])
      groups.set(key, [...(groups.get(key) ?? []), r]);
  const title = (key: string) => key.startsWith("console:") ? consoleName(key.slice(8)) : t(`kinds.${key.slice(5) as (typeof KINDS)[number]}`);
  const sorted = [...groups.entries()].sort(([a], [b]) => (a[0] === b[0] ? title(a).localeCompare(title(b)) : a < b ? -1 : 1));

  return (
    <div className="content">
      <form className="toolbar" onSubmit={(e) => { e.preventDefault(); draft.mutate(); }}>
        <input style={{ flex: 1 }} aria-label={t("repoUrl")} placeholder={t("repoPlaceholder")} value={url} onChange={(e) => setUrl(e.target.value)} />
        <button className="ot-btn ot-btn-primary" type="submit" disabled={!url.trim() || draft.isPending}>{t("fromRepo")}</button>
        <span className="small muted tabular toolbar-total" aria-live="polite">{recipes.data ? t("total", { count: recipes.data.length }) : ""}</span>
      </form>
      {[draft.error, open.error, recipes.error, importMain.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert">{describe(e)}</p>)}
      {editing && <RecipeForm key={String(editing.recipe.id)} recipe={editing.recipe} create={editing.create} onDone={() => setEditing(null)} />}
      {recipes.data?.length === 0 && (
        <section className="panel">
          <p className="muted" style={{ margin: 0 }}>{t("empty")}</p>
          <div><button className="ot-btn" type="button" disabled={importMain.isPending} onClick={() => importMain.mutate()}>{t("importMain")}</button></div>
        </section>
      )}
      {sorted.map(([key, list]) => (
        <section key={key} className="panel">
          <h2 className="small muted" style={{ margin: 0 }}>{title(key)}</h2>
          <div className="chips">
            {list.map((r) => (
              <button key={r.id} type="button" className="chip" aria-pressed={editing?.recipe.id === r.id} disabled={open.isPending} onClick={() => open.mutate(r.id)}
                title={`${r.id} · ${t(`kinds.${r.kind as (typeof KINDS)[number]}`)}`}>
                {r.name || r.id}
                {r.local && <span className="small muted" title={t("localHint")}> · {t("local")}</span>}
              </button>
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

/** El formulario de una receta: un control por campo y lo que no tiene uno (portable, options, perGame…) como JSON. */
function RecipeForm({ recipe, create, onDone }: { recipe: Recipe; create: boolean; onDone: () => void }) {
  const t = useTranslations("emulators");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [r, setR] = useState<Recipe>(recipe);
  const [lists, setLists] = useState<Record<string, string>>(() =>
    Object.fromEntries(LISTS.map((k) => [k, ((recipe[k] as string[] | undefined) ?? []).join(", ")])));
  const [others, setOthers] = useState(() => JSON.stringify(Object.fromEntries(Object.entries(recipe).filter(([k]) => !FORM.has(k))), null, 2));
  const [badOthers, setBadOthers] = useState(false);
  const version = (r.version ?? {}) as { from?: string; value?: string };
  const updates = (r.updates ?? {}) as Record<string, unknown> & { source?: string; repo?: string; host?: string; dev?: boolean };

  // Un campo vacío se quita; uno que ya estaba conserva su lugar en el archivo.
  const set = (key: string, value: unknown) => setR((old) => {
    const next = { ...old };
    if (empty(value)) delete next[key]; else next[key] = value;
    return next;
  });
  const setUpdates = (patch: Partial<typeof updates>) => {
    const next = Object.fromEntries(Object.entries({ ...updates, ...patch }).filter(([, v]) => !empty(v)));
    set("updates", next.source ? next : undefined);
  };

  /** La receta entera, con el orden de sus claves: las del formulario, luego las del JSON y al final las nuevas. */
  const build = (): Recipe | null => {
    let extra: Recipe;
    try { extra = JSON.parse(others || "{}"); } catch { return null; }
    const fields: Recipe = { ...r };
    for (const k of LISTS) fields[k] = lists[k].split(",").map((s) => s.trim()).filter(Boolean);
    const out: Recipe = {};
    for (const k of new Set([...Object.keys(recipe), ...Object.keys(fields), ...Object.keys(extra)])) {
      const v = FORM.has(k) ? fields[k] : extra[k];
      if (!empty(v)) out[k] = v;
    }
    return out;
  };
  const save = useMutation({
    mutationFn: (out: Recipe) => request<RecipeInfo[]>(`/api/recipes/${encodeURIComponent(String(out.id ?? ""))}?create=${create}`, json("PUT", { json: JSON.stringify(out, null, 2) })),
    onSuccess: (list) => { cache.setQueryData(["recipes"], list); onDone(); },
  });
  const submit = () => {
    const out = build();
    setBadOthers(!out);
    if (out) save.mutate(out);
  };

  const text = (key: string, label = t(`field.${key}` as "field.name")) => (
    <label className="field" key={key}>
      <span className="small muted">{label}</span>
      <input value={String(r[key] ?? "")} onChange={(e) => set(key, e.target.value)} />
    </label>
  );

  return (
    <form className="panel" onSubmit={(e) => { e.preventDefault(); submit(); }}>
      <h2 style={{ margin: 0 }}>{create ? t("newTitle") : t("editTitle", { id: String(recipe.id ?? "") })}</h2>
      <div className="fields-row">
        <label className="field">
          <span className="small muted">{t("field.id")}</span>
          <input value={String(r.id ?? "")} readOnly={!create} onChange={(e) => set("id", e.target.value)} />
        </label>
        <label className="field">
          <span className="small muted">{t("field.kind")}</span>
          <select value={String(r.kind ?? "emulator")} onChange={(e) => set("kind", e.target.value)}>
            {KINDS.map((k) => <option key={k} value={k}>{t(`kinds.${k}`)}</option>)}
          </select>
        </label>
        {TEXT.map((k) => text(k))}
      </div>
      <div className="fields-row">
        {LISTS.map((k) => (
          <label className="field" key={k}>
            <span className="small muted">{t(`field.${k}`)}</span>
            <input value={lists[k]} placeholder={t("commas")} onChange={(e) => setLists({ ...lists, [k]: e.target.value })} />
          </label>
        ))}
      </div>
      <div className="fields-row">
        <label className="field">
          <span className="small muted">{t("field.version")}</span>
          <select value={version.from ?? ""} onChange={(e) => set("version", e.target.value ? { from: e.target.value, ...(e.target.value === "fixed" && version.value ? { value: version.value } : {}) } : undefined)}>
            <option value="">—</option>
            {VERSIONS.map((v) => <option key={v} value={v}>{t(`versionFrom.${v}`)}</option>)}
          </select>
        </label>
        {version.from === "fixed" && (
          <label className="field">
            <span className="small muted">{t("field.versionValue")}</span>
            <input value={version.value ?? ""} onChange={(e) => set("version", { from: "fixed", value: e.target.value })} />
          </label>
        )}
        <label className="field">
          <span className="small muted">{t("field.source")}</span>
          <select value={updates.source ?? ""} onChange={(e) => setUpdates({ source: e.target.value })}>
            <option value="">—</option>
            {SOURCES.map((s) => <option key={s} value={s}>{t(`sources.${s}`)}</option>)}
          </select>
        </label>
        {(updates.source === "github" || updates.source === "forgejo") && (
          <label className="field">
            <span className="small muted">{t("field.repo")}</span>
            <input value={updates.repo ?? ""} placeholder="dueño/repo" onChange={(e) => setUpdates({ repo: e.target.value })} />
          </label>
        )}
        {updates.source === "forgejo" && (
          <label className="field">
            <span className="small muted">{t("field.host")}</span>
            <input value={updates.host ?? ""} placeholder="codeberg.org" onChange={(e) => setUpdates({ host: e.target.value })} />
          </label>
        )}
      </div>
      <div className="fields-row">
        {updates.source && (
          <label className="sentence">
            <input type="checkbox" checked={!!updates.dev} onChange={(e) => setUpdates({ dev: e.target.checked })} />
            {t("field.dev")}
          </label>
        )}
        {FLAGS.map((k) => (
          <label className="sentence" key={k}>
            <input type="checkbox" checked={!!r[k]} onChange={(e) => set(k, e.target.checked)} />
            {t(`field.${k}`)}
          </label>
        ))}
      </div>
      <label className="field">
        <span className="small muted">{t("others")}</span>
        <textarea rows={10} spellCheck={false} style={{ fontFamily: "monospace" }} value={others} onChange={(e) => setOthers(e.target.value)} />
      </label>
      <p className="small muted" style={{ margin: 0 }}>{t("checkNote")}</p>
      {badOthers && <p className="error small" role="alert" style={{ margin: 0 }}>{t("badOthers")}</p>}
      {save.error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(save.error)}</p>}
      <div className="dialog-actions">
        <button className="ot-btn" type="button" onClick={onDone}>{t("cancel")}</button>
        <button className="ot-btn ot-btn-primary" type="submit" disabled={save.isPending}>{save.isPending ? t("checking") : t("save")}</button>
      </div>
    </form>
  );
}
