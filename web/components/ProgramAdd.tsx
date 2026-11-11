"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useConsoleName, useConsoles } from "@/components/Catalog";
import { RecipeEditor } from "@/components/RecipeEditor";
import { useSearch } from "@/components/SagaEditing";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeText, RepoRelease } from "@/lib/types";

type Recipe = Record<string, unknown>;

export const KINDS = ["port", "emulator", "complement", "tool"] as const;
export type Kind = (typeof KINDS)[number];
/** El orden de «¿Qué vas a agregar?» en el diseño (#166). */
const ADD_ORDER: Kind[] = ["emulator", "port", "complement", "tool"];

/** El archivo de Windows de la release más nueva que tenga uno; entre varios, el que dice «win» (lo mismo que elige el servidor al armar el borrador). */
function windowsFile(releases: RepoRelease[]) {
  for (const rel of releases) {
    const files = rel.files.filter((f) => f.windows);
    if (files.length) return { release: rel, file: (files.find((f) => /win/i.test(f.name)) ?? files[0]).name };
  }
  return null;
}

/** Lo que no es de su tipo se quita al guardar (por si se cambió de tipo a mitad), igual que las listas que quedaron vacías. */
function clean(r: Recipe): Recipe {
  const drop: Record<string, string[]> = { emulator: ["game", "port", "parent", "link"], port: ["consoles", "baseFor", "parent", "link"], complement: ["consoles", "baseFor", "game", "port"], tool: ["consoles", "baseFor", "game", "port", "parent", "link"] };
  return Object.fromEntries(Object.entries(r).filter(([k, v]) => !(drop[String(r.kind)] ?? []).includes(k) && !(Array.isArray(v) && v.length === 0)));
}

/**
 * Agregar programa (#166): primero qué es (los 4 tipos) y luego 3 pasos. Origen: se pega el repo y el archivo de Windows se detecta solo
 * (se puede cambiar). El segundo paso depende del tipo: las consolas de un emulador (los formatos salen de la consola), el juego de un port,
 * el programa al que se suma un complemento o para qué sirve una herramienta. Guardar muestra el resumen y, plegados, los ~40 campos de la
 * receta (RecipeEditor). Al guardar el servidor valida el esquema y que la fuente tenga una release con un archivo que calce, como siempre.
 */
export function ProgramAdd({ onCancel, onSaved }: { onCancel: () => void; onSaved: (name: string, kind: Kind) => void }) {
  const t = useTranslations("programs");
  const te = useTranslations("emulators");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const consoleName = useConsoleName();
  const [step, setStep] = useState(1);
  const [kind, setKind] = useState<Kind>("emulator");
  const [url, setUrl] = useState("");
  const [asked, setAsked] = useState("");
  const [file, setFile] = useState<string | null>(null); // null: el que se detectó
  const [edited, setEdited] = useState<Recipe | null>(null); // null: el borrador del servidor tal cual

  const ask = (text: string) => { if (text.trim() !== asked) { setAsked(text.trim()); setFile(null); setEdited(null); } };
  const releases = useQuery({
    queryKey: ["recipes", "releases", asked],
    queryFn: () => request<RepoRelease[]>(`/api/recipes/releases?url=${encodeURIComponent(asked)}`),
    enabled: !!asked, staleTime: 15 * 60_000, retry: false,
  });
  const found = releases.data ? windowsFile(releases.data) : null;
  const chosen = file ?? found?.file ?? null;
  const draft = useQuery({
    queryKey: ["recipes", "draft", asked, chosen],
    queryFn: () => request<RecipeText>("/api/recipes/draft", { method: "POST", body: JSON.stringify({ url: asked, file: chosen }) }).then((r) => JSON.parse(r.json) as Recipe),
    enabled: !!chosen, staleTime: Infinity, retry: false,
  });
  const base = edited ?? draft.data ?? null;
  const recipe: Recipe | null = base && { ...base, kind };
  const change = (patch: Recipe) => recipe && setEdited({ ...recipe, ...patch });

  const save = useMutation({
    mutationFn: (out: Recipe) => request<RecipeInfo[]>(`/api/recipes/${encodeURIComponent(String(out.id ?? ""))}?create=true`, { method: "PUT", body: JSON.stringify({ json: JSON.stringify(out, null, 2) }) }),
    onSuccess: (list, out) => { cache.setQueryData(["recipes"], list); cache.invalidateQueries({ queryKey: ["recipes", "status"] }); onSaved(String(out.name || out.id), kind); },
  });

  const consoles = (recipe?.consoles as string[] | undefined) ?? [];
  const second = kind === "emulator" ? consoles.length > 0 : kind === "port" ? !!recipe?.game : kind === "complement" ? !!recipe?.parent : !!recipe?.folder;
  const ready = [true, !!recipe, !!recipe && second];
  const labels = [t("wizard.origin"), t(`kind.${kind}.step`), te("save")];
  const release = releases.data?.find((r) => r.files.some((f) => f.name === chosen)) ?? found?.release;
  const summary = kind === "emulator" ? consoles.map(consoleName).join(", ")
    : kind === "port" ? [recipe?.game, recipe?.folder].filter(Boolean).join(" · ") : String(recipe?.folder ?? "");

  return (
    <div style={{ display: "grid", gap: 20, maxWidth: 760 }}>
      <nav aria-label={t("wizard.steps")} className="program-steps">
        {labels.map((l, i) => (
          <button key={i} type="button" className={`program-step${step > i + 1 ? " ok" : ""}`} aria-current={step === i + 1 ? "step" : undefined}
            disabled={!ready.slice(0, i + 1).every(Boolean)} onClick={() => setStep(i + 1)}>
            <span className="program-step-n">{i + 1}</span>{l}
          </button>
        ))}
      </nav>

      {step === 1 && (
        <section className="panel" style={{ gap: 16 }}>
          <h2 style={{ margin: 0 }}>{t("wizard.what")}</h2>
          <div className="program-kinds" role="group" aria-label={te("field.kind")}>
            {ADD_ORDER.map((k) => (
              <button key={k} type="button" className="program-kind" aria-pressed={kind === k} onClick={() => setKind(k)}>
                <b>{te(`kinds.${k}`)}</b><span className="small muted">{t(`kind.${k}.desc`)}</span>
              </button>
            ))}
          </div>
          <form className="field" onSubmit={(e) => { e.preventDefault(); ask(url); }}>
            <label className="field">
              <span className="small muted">{t("wizard.repo")}</span>
              <input value={url} spellCheck={false} placeholder={te("repoPlaceholder")} onChange={(e) => setUrl(e.target.value)} onBlur={() => ask(url)}
                onPaste={(e) => { const text = e.clipboardData.getData("text"); if (text) { e.preventDefault(); setUrl(text.trim()); ask(text); } }} />
            </label>
          </form>
          {(releases.isFetching || draft.isFetching) && <span className="small muted" role="status">{t("wizard.looking")}</span>}
          {[releases.error, draft.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert" style={{ margin: 0 }}>{describe(e)}</p>)}
          {releases.data && !found && <p className="error small" role="alert" style={{ margin: 0 }}>{t("wizard.noWindows")}</p>}
          {recipe && release && (
            <>
              <span className="small program-ok" role="status">{t("wizard.found", { source: te(`sources.${String((recipe.updates as Recipe | undefined)?.source ?? "github")}` as "sources.github"), tag: release.tag })}</span>
              <label className="field">
                <span className="small muted">{te("field.name")}</span>
                <input value={String(recipe.name ?? "")} onChange={(e) => change({ name: e.target.value })} />
              </label>
              <div className="field">
                <label className="field">
                  <span className="small muted">{t("wizard.file")}</span>
                  <select value={chosen ?? ""} onChange={(e) => { setFile(e.target.value); setEdited(null); }}>
                    {release.files.map((f) => <option key={f.name} value={f.name}>{f.name}</option>)}
                  </select>
                </label>
                <span className="small"><code>{String(recipe.asset ?? "")}</code> <span className="program-ok">{t("wizard.fileMatches", { file: chosen ?? "" })}</span></span>
                <span className="small muted">{t("wizard.fileHint")}</span>
              </div>
            </>
          )}
        </section>
      )}

      {step === 2 && recipe && (
        <section className="panel" style={{ gap: 16 }}>
          <h2 style={{ margin: 0 }}>{t(`kind.${kind}.question`)}</h2>
          {kind === "emulator" && <ConsolePick values={consoles} onChange={(v) => change({ consoles: v, folder: v[0] ? consoleName(v[0]) : "" })} />}
          {kind === "port" && <GamePick recipe={recipe} onPick={(game, console) => change({ game, folder: console ? consoleName(console) : "", port: asked })} />}
          {kind === "complement" && <ParentPick parent={String(recipe.parent ?? "")} onPick={(r) => change({ parent: r.id, folder: r.name || r.id })} />}
          {kind === "tool" && (
            <>
              <label className="field">
                <span className="small muted">{t("wizard.toolUse")}</span>
                <input value={String(recipe.folder ?? "")} onChange={(e) => change({ folder: e.target.value })} />
              </label>
              <p className="small muted" style={{ margin: 0 }}>{t("wizard.toolHint")}</p>
            </>
          )}
        </section>
      )}

      {step === 3 && recipe && (
        <section className="panel" style={{ gap: 12 }}>
          <h2 style={{ margin: 0 }}>{t("wizard.review")}</h2>
          <table className="t"><tbody>
            <tr><td className="muted">{te("field.name")}</td><td><b>{String(recipe.name ?? "")}</b> · {te(`kinds.${kind}`).toLowerCase()}</td></tr>
            <tr><td className="muted">{t("wizard.download")}</td><td>{asked.replace(/^https?:\/\//, "")} · {chosen}</td></tr>
            <tr><td className="muted">{t(`kind.${kind}.column`)}</td><td>{summary || "—"}</td></tr>
          </tbody></table>
          <details className="fold-line">
            <summary className="small">{t("wizard.more")}</summary>
            <RecipeEditor recipe={recipe} create onChange={(r) => { if (KINDS.includes(r.kind as Kind)) setKind(r.kind as Kind); setEdited(r); }} />
          </details>
          {save.error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(save.error)}</p>}
        </section>
      )}

      <div className="toolbar">
        <button className="ot-btn" type="button" onClick={onCancel}>{te("cancel")}</button>
        {step > 1 && <button className="ot-btn" type="button" onClick={() => setStep(step - 1)}>{t("wizard.back")}</button>}
        <span style={{ flex: 1 }} />
        {step < 3
          ? <button className="ot-btn ot-btn-primary" type="button" disabled={!ready[step]} onClick={() => setStep(step + 1)}>{t("wizard.next")}</button>
          : <button className="ot-btn ot-btn-primary" type="button" disabled={save.isPending || !recipe} onClick={() => recipe && save.mutate(clean(recipe))}>{save.isPending ? te("checking") : te("save")}</button>}
      </div>
    </div>
  );
}

/** Las consolas del catálogo como chips: los formatos que abre salen de la consola (Otteretro los conoce), así que no se piden. */
function ConsolePick({ values, onChange }: { values: string[]; onChange: (v: string[]) => void }) {
  const t = useTranslations("programs");
  const te = useTranslations("emulators");
  const all = useConsoles().data ?? [];
  return (
    <>
      <div className="chips" role="group" aria-label={te("field.consoles")}>
        {all.map((c) => (
          <button key={c.id} type="button" className="chip" aria-pressed={values.includes(c.id)}
            onClick={() => onChange(values.includes(c.id) ? values.filter((x) => x !== c.id) : [...values, c.id])}>{c.name}</button>
        ))}
      </div>
      <p className="small muted" style={{ margin: 0 }}>{values.length ? t("wizard.formats") : t("wizard.pickConsole")}</p>
    </>
  );
}

/** El juego de un port: se busca en el catálogo; su consola (la primera) da la carpeta. */
function GamePick({ recipe, onPick }: { recipe: Recipe; onPick: (game: string, console?: string) => void }) {
  const t = useTranslations("programs");
  const [text, setText] = useState("");
  const search = useSearch(text);
  return (
    <>
      <label className="field">
        <span className="small muted">{t("wizard.gameSearch")}</span>
        <input type="search" value={text} spellCheck={false} onChange={(e) => setText(e.target.value)} />
      </label>
      {text.trim().length >= 2 && (
        <div className="chips">
          {search.data?.games.map((g) => (
            <button key={g.id} className="chip" type="button" onClick={() => { onPick(g.name, g.platforms[0]); setText(""); }}>{g.name}{g.year ? ` (${g.year})` : ""}</button>
          ))}
        </div>
      )}
      {!!recipe.game && (
        <div className="panel program-pick">
          <span style={{ flex: 1 }}><b>{String(recipe.game)}</b><span className="small muted" style={{ display: "block" }}>{String(recipe.folder ?? "")}</span></span>
          <span className="ot-badge ot-badge-have">{t("wizard.chosen")}</span>
        </div>
      )}
      <p className="small muted" style={{ margin: 0 }}>{t("wizard.portHint")}</p>
    </>
  );
}

/** El programa al que se suma un complemento: un emulador o un port del catálogo, por nombre. */
function ParentPick({ parent, onPick }: { parent: string; onPick: (r: RecipeInfo) => void }) {
  const t = useTranslations("programs");
  const recipes = useQuery({ queryKey: ["recipes"], queryFn: () => request<RecipeInfo[]>("/api/recipes") });
  const [text, setText] = useState("");
  const q = text.trim().toLowerCase();
  const chosen = recipes.data?.find((r) => r.id === parent);
  const matches = (recipes.data ?? []).filter((r) => r.kind !== "complement" && r.id !== parent && q.length >= 2 && (r.name || r.id).toLowerCase().includes(q)).slice(0, 8);
  return (
    <>
      <label className="field">
        <span className="small muted">{t("wizard.parentSearch")}</span>
        <input type="search" value={text} spellCheck={false} onChange={(e) => setText(e.target.value)} />
      </label>
      <div className="chips" role="group" aria-label={t("wizard.parentSearch")}>
        {chosen && <button type="button" className="chip" aria-pressed>{chosen.name || chosen.id}</button>}
        {matches.map((r) => <button key={r.id} type="button" className="chip" aria-pressed={false} onClick={() => { onPick(r); setText(""); }}>{r.name || r.id}</button>)}
      </div>
      <p className="small muted" style={{ margin: 0 }}>{t("wizard.parentHint")}</p>
    </>
  );
}
