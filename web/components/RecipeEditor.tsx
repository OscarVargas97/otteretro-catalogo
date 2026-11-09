"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useConsoleName, useConsoles } from "@/components/Catalog";
import { Tabs } from "@/components/Tabs";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeText, RepoRelease } from "@/lib/types";

type Recipe = Record<string, unknown>;
type Schema = { type?: string; enum?: string[]; properties?: Record<string, Schema>; items?: Schema; $ref?: string };
type Path = (string | number)[];

/** Qué campos de primer nivel van en cada sección (decisión 368). Uno del esquema que no esté en ninguna cae en Opciones. */
const SECTIONS = {
  basic: ["id", "kind", "name", "folder", "game", "parent", "link", "detect", "version", "license", "experimental", "devOnly"],
  download: ["updates", "asset", "saveAs", "downloads"],
  consoles: ["consoles", "baseFor", "formats", "install"],
  perGame: ["perGame", "input", "saves", "config", "content", "gamePaths", "contentPaths"],
  options: ["options", "optionsUpTo", "optionFlags", "settings", "portable"],
} as const;
type Section = keyof typeof SECTIONS;
/** Campos que solo tienen sentido en un tipo de receta: se ven en ese tipo o si ya traen algo. */
const ONLY: Record<string, string> = { game: "port", parent: "complement", link: "complement" };
/** Los enum que ya tienen nombre en los catálogos de textos; los demás (ini, json, github…) se muestran tal cual. */
const ENUM_LABELS: Record<string, string> = { kind: "kinds", "updates.source": "sources", "version.from": "versionFrom" };

const json = (method: string, body: unknown) => ({ method, body: JSON.stringify(body) });
const empty = (v: unknown) => v === undefined || v === "" || v === false || (Array.isArray(v) && v.length === 0)
  || (typeof v === "object" && v !== null && !Array.isArray(v) && Object.keys(v).length === 0);

/** Cambia un valor dentro de la receta. Un campo vacío se quita (y el objeto que queda vacío también); uno que ya estaba conserva su lugar. */
function setIn(obj: unknown, path: Path, value: unknown): unknown {
  if (path.length === 0) return value;
  const [k, ...rest] = path;
  const copy = (Array.isArray(obj) ? [...obj] : { ...(obj as Recipe | undefined) }) as Record<string | number, unknown>;
  const v = setIn(copy[k], rest, value);
  if (!Array.isArray(copy) && empty(v)) delete copy[k]; else copy[k] = v;
  return copy;
}

/** Sigue un `$ref` local (`#/properties/settings/items`), lo único que usa el esquema. */
function resolve(root: Schema, s: Schema): Schema {
  if (!s.$ref) return s;
  return s.$ref.slice(2).split("/").reduce<Record<string, unknown>>((node, key) => node[key] as Record<string, unknown>, root as Record<string, unknown>) as Schema;
}

/**
 * El editor de una receta (#111, decisión 368), dibujado desde `receta.schema.json` (el que el servidor usa para validar): una pestaña por
 * sección y cada campo según su tipo (lista para un enum, casilla, chips para una lista de textos, filas para una de objetos). El JSON
 * entero solo se edita en la vista avanzada, plegada al pie. Guardar valida esquema y fuente en el servidor, como antes.
 */
export function RecipeEditor({ recipe, create, onDone }: { recipe: Recipe; create: boolean; onDone: () => void }) {
  const t = useTranslations("emulators");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const schema = useQuery({ queryKey: ["recipes", "schema"], queryFn: () => request<RecipeText>("/api/recipes/schema").then((r) => JSON.parse(r.json) as Schema), staleTime: Infinity });
  const [r, setR] = useState<Recipe>(recipe);
  const [section, setSection] = useState<Section>("basic");
  const [text, setText] = useState<string | null>(null); // el JSON de la vista avanzada mientras se escribe; null: el de la receta
  const [bad, setBad] = useState(false);
  const save = useMutation({
    mutationFn: (out: Recipe) => request<RecipeInfo[]>(`/api/recipes/${encodeURIComponent(String(out.id ?? ""))}?create=${create}`, json("PUT", { json: JSON.stringify(out, null, 2) })),
    onSuccess: (list) => { cache.setQueryData(["recipes"], list); cache.invalidateQueries({ queryKey: ["recipes", "status"] }); onDone(); },
  });
  // Un JSON a medio corregir se conserva; si no, la vista avanzada vuelve a mostrar la receta con el cambio.
  const set = (path: Path, value: unknown) => { setR((old) => setIn(old, path, value) as Recipe); if (!bad) setText(null); };

  const root = schema.data;
  const props = root?.properties ?? {};
  const placed = new Set<string>(Object.values(SECTIONS).flat());
  const keysOf = (s: keyof typeof SECTIONS) => [...SECTIONS[s], ...(s === "options" ? Object.keys(props).filter((k) => !placed.has(k)) : [])]
    .filter((k) => props[k] && (!ONLY[k] || r.kind === ONLY[k] || r[k] !== undefined));

  const label = (name: string) => t.has(`field.${name}` as "field.name") ? t(`field.${name}` as "field.name") : name;
  const enumLabel = (key: string, v: string) => ENUM_LABELS[key] ? t(`${ENUM_LABELS[key]}.${v}` as "kinds.emulator") : v;

  /** Un campo según su esquema. `key` es la ruta sin índices (`updates.source`), para los nombres de los enum. */
  const field = (name: string, s0: Schema, path: Path, value: unknown, key: string): React.ReactNode => {
    const s = resolve(root!, s0);
    if (s.enum) return (
      <label className="field" key={name}>
        <span className="small muted">{label(name)}</span>
        <select value={String(value ?? "")} onChange={(e) => set(path, e.target.value)}>
          <option value="">—</option>
          {s.enum.map((v) => <option key={v} value={v}>{enumLabel(key, v)}</option>)}
        </select>
      </label>
    );
    if (s.type === "boolean") return (
      <label className="sentence" key={name}>
        <input type="checkbox" checked={!!value} onChange={(e) => set(path, e.target.checked)} />
        {label(name)}
      </label>
    );
    if (s.type === "array" && resolve(root!, s.items ?? {}).type === "string") return (
      <Chips key={name} label={label(name)} values={(value as string[] | undefined) ?? []} onChange={(v) => set(path, v)}
        consoles={key === "consoles"} choices={key === "baseFor" ? (r.consoles as string[] | undefined) ?? [] : undefined} />
    );
    if (s.type === "array") {
      const rows = (value as Recipe[] | undefined) ?? [];
      const item = resolve(root!, s.items ?? {});
      return (
        <fieldset className="recipe-group" key={name}>
          <legend className="small muted">{label(name)}</legend>
          {rows.map((row, i) => (
            <div className="fields-row recipe-row" key={i}>
              {Object.entries(item.properties ?? {}).map(([k, sub]) => field(k, sub, [...path, i, k], row?.[k], `${key}.${k}`))}
              <button className="ot-btn" type="button" aria-label={t("removeRow", { name: label(name), n: i + 1 })} onClick={() => set(path, rows.filter((_, j) => j !== i))}>{t("remove")}</button>
            </div>
          ))}
          <div><button className="ot-btn" type="button" onClick={() => set(path, [...rows, {}])}>{t("addRow")}</button></div>
        </fieldset>
      );
    }
    if (s.type === "object") return (
      <fieldset className="recipe-group" key={name}>
        <legend className="small muted">{label(name)}</legend>
        <div className="fields-row">
          {Object.entries(s.properties ?? {}).map(([k, sub]) => field(k, sub, [...path, k], (value as Recipe | undefined)?.[k], `${key}.${k}`))}
        </div>
      </fieldset>
    );
    return (
      <div className="field" key={name}>
        <label className="field">
          <span className="small muted">{label(name)}</span>
          <input value={String(value ?? "")} readOnly={key === "id" && !create} spellCheck={false} onChange={(e) => set(path, e.target.value)} />
        </label>
        {key === "asset" && <AssetTest pattern={String(value ?? "")} updates={r.updates as Recipe | undefined} />}
      </div>
    );
  };

  const sections = Object.keys(SECTIONS) as Section[];

  return (
    <form className="panel" onSubmit={(e) => { e.preventDefault(); if (!bad) save.mutate(r); }}>
      <h2 style={{ margin: 0 }}>{create ? t("newTitle") : t("editTitle", { id: String(recipe.id ?? "") })}</h2>
      <Tabs tabs={sections.map((s) => ({ id: s, label: t(`sections.${s}`) }))} current={section} onSelect={(id) => setSection(id as Section)} label={t("sectionsLabel")} panel="recipe-section" />
      <div id="recipe-section" role="tabpanel" aria-labelledby={`tab-${section}`} style={{ display: "grid", gap: 12 }}>
        {schema.error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(schema.error)}</p>}
        {root && <div className="fields-row">{keysOf(section).map((k) => field(k, props[k], [k], r[k], k))}</div>}
      </div>
      <details className="fold-line">
        <summary className="small muted">{t("jsonView")}</summary>
        <label className="field">
          <span className="small muted">{t("jsonHint")}</span>
          <textarea rows={20} spellCheck={false} style={{ fontFamily: "monospace" }} value={text ?? JSON.stringify(r, null, 2)} onChange={(e) => {
            setText(e.target.value);
            try { const v = JSON.parse(e.target.value); if (!v || typeof v !== "object" || Array.isArray(v)) throw v; setR(v); setBad(false); } catch { setBad(true); }
          }} />
        </label>
      </details>
      <p className="small muted" style={{ margin: 0 }}>{t("checkNote")}</p>
      {bad && <p className="error small" role="alert" style={{ margin: 0 }}>{t("badJson")}</p>}
      {save.error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(save.error)}</p>}
      <div className="dialog-actions">
        <button className="ot-btn" type="button" onClick={onDone}>{t("cancel")}</button>
        <button className="ot-btn ot-btn-primary" type="submit" disabled={save.isPending || bad}>{save.isPending ? t("checking") : t("save")}</button>
      </div>
    </form>
  );
}

/**
 * Una lista de textos como chips: Enter (o salir del campo) agrega, × quita. En consolas sugiere los ids del catálogo y muestra sus nombres;
 * con `choices` (baseFor) solo se eligen entre esas.
 */
function Chips({ label, values, onChange, consoles, choices }: { label: string; values: string[]; onChange: (v: string[]) => void; consoles?: boolean; choices?: string[] }) {
  const t = useTranslations("emulators");
  const name = useConsoleName();
  const all = useConsoles().data;
  const [draft, setDraft] = useState("");
  const shownAs = (v: string) => consoles || choices ? name(v) : v;
  const add = () => {
    const v = draft.trim();
    if (v && !values.includes(v)) onChange([...values, v]);
    setDraft("");
  };

  if (choices) return (
    <div className="field">
      <span className="small muted">{label}</span>
      <div className="chips" role="group" aria-label={label}>
        {choices.length === 0 && <span className="small muted">{t("noConsoles")}</span>}
        {choices.map((c) => (
          <button key={c} type="button" className="chip" aria-pressed={values.includes(c)}
            onClick={() => onChange(values.includes(c) ? values.filter((x) => x !== c) : [...values, c])}>{shownAs(c)}</button>
        ))}
      </div>
    </div>
  );

  const list = consoles ? `recipe-consoles` : undefined;
  return (
    <div className="field">
      <span className="small muted">{label}</span>
      <div className="chips" role="group" aria-label={label}>
        {values.map((v) => (
          <span key={v} className="chip">
            {shownAs(v)}
            <button type="button" className="chip-remove" aria-label={t("removeChip", { value: shownAs(v) })} onClick={() => onChange(values.filter((x) => x !== v))}>×</button>
          </span>
        ))}
        <input aria-label={label} placeholder={t("chipAdd")} value={draft} list={list} spellCheck={false} onChange={(e) => setDraft(e.target.value)} onBlur={add}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); add(); } }} />
        {list && <datalist id={list}>{all?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</datalist>}
      </div>
    </div>
  );
}

/** El patrón del archivo probado en vivo contra los archivos de la última release del repo de la receta (decisión 368). */
function AssetTest({ pattern, updates }: { pattern: string; updates?: Recipe }) {
  const t = useTranslations("emulators");
  const repo = typeof updates?.repo === "string" && /^[^/\s]+\/[^/\s]+$/.test(updates.repo) ? updates.repo : null;
  const url = !repo ? null : updates?.source === "github" ? `https://github.com/${repo}` : updates?.source === "forgejo" && updates.host ? `https://${updates.host}/${repo}` : null;
  const releases = useQuery({
    queryKey: ["recipes", "releases", url],
    queryFn: () => request<RepoRelease[]>(`/api/recipes/releases?url=${encodeURIComponent(url!)}`),
    enabled: !!url, staleTime: 15 * 60_000, retry: false,
  });
  if (!url) return <span className="small muted">{t("assetNoRepo")}</span>;
  if (releases.isPending) return <span className="small muted">{t("assetLoading")}</span>;
  const latest = releases.data?.[0];
  if (!latest) return <span className="small muted">{t("assetNoRelease")}</span>;
  // ponytail: el servidor valida con Regex de .NET y acá con el de JS; los patrones de las recetas usan lo que comparten los dos.
  let re: RegExp | null = null;
  try { re = pattern ? new RegExp(pattern) : null; } catch { return <span className="error small" role="alert">{t("assetBadPattern")}</span>; }
  const hits = re ? latest.files.filter((f) => re.test(f.name)).map((f) => f.name) : [];
  return (
    <span className={`small ${re && hits.length === 0 ? "error" : "muted"}`} role="status">
      {!re ? t("assetFiles", { tag: latest.tag, files: latest.files.map((f) => f.name).join(" · ") })
        : hits.length ? t("assetMatches", { tag: latest.tag, files: hits.join(" · ") })
          : t("assetNoMatch", { tag: latest.tag, files: latest.files.map((f) => f.name).join(" · ") })}
    </span>
  );
}
