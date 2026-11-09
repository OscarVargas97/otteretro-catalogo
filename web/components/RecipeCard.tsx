"use client";

import { useFormatter, useTranslations } from "next-intl";
import { useConsoleName } from "@/components/Catalog";
import type { RecipeInfo, RecipeStatus } from "@/lib/types";

/**
 * La tarjeta de una receta (#108, decisión 365), con Emuladores de Otteretro como modelo (369): nombre, consolas y tipo; la última
 * release y su fecha; si el patrón calza con ella; y si está publicada, cambiada sin publicar o es local. Sin estado todavía (se está
 * consultando), esa parte no se muestra. Los forks llegan con #98.
 */
export function RecipeCard({ recipe, status, editing, busy, onEdit }: {
  recipe: RecipeInfo; status?: RecipeStatus; editing: boolean; busy: boolean; onEdit: () => void;
}) {
  const t = useTranslations("emulators");
  const format = useFormatter();
  const consoleName = useConsoleName();
  const state = recipe.local ? "local" : status?.state;
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
      </div>
    </section>
  );
}
