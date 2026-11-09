"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useConsoleName } from "@/components/Catalog";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeInfo, RecipeStatus, RecipeTest } from "@/lib/types";

/**
 * La tarjeta de una receta (#108, decisión 365), con Emuladores de Otteretro como modelo (369): nombre, consolas y tipo; la última
 * release y su fecha; si el patrón calza con ella; y si está publicada, cambiada sin publicar o es local. Sin estado todavía (se está
 * consultando), esa parte no se muestra. Los forks llegan con #98.
 */
export function RecipeCard({ recipe, status, editing, busy, onEdit, onDuplicate }: {
  recipe: RecipeInfo; status?: RecipeStatus; editing: boolean; busy: boolean; onEdit: () => void; onDuplicate: () => void;
}) {
  const t = useTranslations("emulators");
  const format = useFormatter();
  const consoleName = useConsoleName();
  const state = recipe.local ? "local" : status?.state;
  const describe = useDescribeError();
  const cache = useQueryClient();
  const id = encodeURIComponent(recipe.id);
  const test = useMutation({ mutationFn: () => request<RecipeTest>(`/api/recipes/${id}/test`, { method: "POST" }) });
  const discard = useMutation({
    mutationFn: () => request<unknown>(`/api/recipes/${id}`, { method: "DELETE" }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["recipes"] }),
  });
  const result = test.data;
  return (
    <section className="panel emulator-card" style={{ display: "grid", gap: 6 }} aria-label={recipe.name || recipe.id}>
      <div className="version-row">
        <span className="grow">
          <h3 style={{ margin: 0 }}>{recipe.name || recipe.id}</h3>
          <span className="small muted">{t(`kinds.${recipe.kind as "emulator"}`)}{recipe.consoles.length > 0 && ` · ${recipe.consoles.map(consoleName).join(", ")}`}</span>
        </span>
        <span style={{ display: "flex", gap: 6, alignItems: "center", flexWrap: "wrap" }}>
          {status?.tag != null && (
            <span className="ot-badge" title={t("card.latest")}>
              {status.tag}{status.publishedAt && ` · ${format.dateTime(new Date(status.publishedAt), { dateStyle: "medium" })}`}
            </span>
          )}
          {status?.matches === false && <span className="ot-badge ot-badge-danger" title={t("card.noMatchHint")}>{t("card.noMatch")}</span>}
          {status?.matches === true && <span className="ot-badge ot-badge-have">{t("card.matches")}</span>}
          {state === "published" && <span className="ot-badge ot-badge-have">{t("card.published")}</span>}
          {state === "changed" && <span className="ot-badge ot-badge-review" title={t("card.changedHint")}>{t("card.changed")}</span>}
          {state === "local" && <span className="ot-badge ot-badge-info" title={t("localHint")}>{t("local")}</span>}
        </span>
      </div>
      <div className="sentence">
        <button className="ot-btn ot-btn-sm" type="button" aria-pressed={editing} disabled={busy} onClick={onEdit}>{t("card.edit")}</button>
        <button className="ot-btn ot-btn-sm" type="button" disabled={busy} onClick={onDuplicate}>{t("card.duplicate")}</button>
        {recipe.url && <a className="ot-btn ot-btn-sm" href={recipe.url} target="_blank" rel="noreferrer">{t("card.openRepo")}</a>}
        <button className="ot-btn ot-btn-sm" type="button" disabled={test.isPending} onClick={() => test.mutate()}>{test.isPending ? t("card.testing") : t("card.test")}</button>
        {state === "changed" && (
          <button className="ot-btn ot-btn-sm" type="button" disabled={discard.isPending}
            onClick={() => { if (window.confirm(t("card.discardConfirm", { id: recipe.id }))) discard.mutate(); }}>{t("card.discard")}</button>
        )}
      </div>
      {result && (
        <p className="small" style={{ margin: 0 }} role="status">
          {t("card.testResult", { file: result.file, size: (Number(result.size) / 1048576).toFixed(1), tag: result.tag })}{" "}
          {t(result.found === true ? "card.testFound" : result.found === false ? "card.testNotFound" : "card.testUnknown", { detect: result.detect })}
        </p>
      )}
      {[test.error, discard.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert" style={{ margin: 0 }}>{describe(e)}</p>)}
    </section>
  );
}
