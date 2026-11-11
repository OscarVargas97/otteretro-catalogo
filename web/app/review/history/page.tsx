"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import { Empty } from "@/components/Shell";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { Panels } from "@/components/Skeleton";
import { usePaged } from "@/lib/paged";
import type { LearningItem, LearningPage, ReviewPage } from "@/lib/types";

/**
 * Historial de cambios (#164, antes Revisar › Aprendizajes, F2.12): lo que Claude aplicó al catálogo, agrupado por noche y tarea, una
 * línea por cambio; el porqué y la regla aprendida al pulsarla. Se deshace o se corrige con una nota. Llega por tramos al bajar (#177).
 */
export default function History() {
  const t = useTranslations("review");
  const format = useFormatter();
  const [kind, setKind] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const text = query.trim();
  const page = usePaged(["learnings", kind, text], (offset) => `/api/learnings?${new URLSearchParams({ ...(kind && { kind }), ...(text && { q: text }), offset: String(offset) })}`,
    (p: LearningPage) => ({ items: p.items, total: Number(p.total) }));
  // La noche es el día local en que se aplicó; dentro, cada tarea por separado.
  const groups = Map.groupBy(page.items, (l: LearningItem) => `${new Date(l.createdAt).toDateString()}|${l.taskId ?? ""}`);
  return (
    <div className="content" style={{ maxWidth: 980 }}>
      <Link className="small muted" href="/review/">← {t("history.back")}</Link>
      <p className="small muted" style={{ margin: 0 }}>{t("history.intro")}</p>
      {page.data && page.total === 0 && !kind && !text ? <Empty title={t("learningsEmptyTitle")} text={t("learningsEmptyText")} /> : <>
        <div style={{ display: "flex", flexWrap: "wrap", gap: "8px 12px", alignItems: "center" }}>
          <input type="search" aria-label={t("search")} placeholder={t("search")} value={query} onChange={(e) => setQuery(e.target.value)} style={{ flex: "1 1 220px", maxWidth: 320 }} />
          <div className="chips" role="group" aria-label={t("filterType")}>
            {([null, "move", "merge", "rename", "alias"] as const).map((k) => (
              <button key={k ?? "all"} className="chip" type="button" aria-pressed={kind === k} onClick={() => setKind(k)}>
                {k ? t(`kindFilter.${k}`) : t("filterAll")}
              </button>
            ))}
          </div>
          {page.data && <span className="small muted" style={{ marginLeft: "auto" }} aria-live="polite">{t("count", { count: page.total })}</span>}
        </div>
        {[...groups].map(([key, items]) => {
          const first = new Date(items[0].createdAt);
          const task = items[0].taskId;
          return (
            <section key={key} className="panel" style={{ gap: 0 }}>
              <div className="sec-h" style={{ paddingBottom: 4 }}>
                <h2>{format.dateTime(first, { dateStyle: "long" })}</h2>
                <span className="small muted">{task == null ? t("noTask") : t("task", { id: task })} · {format.dateTime(first, { timeStyle: "short" })}</span>
              </div>
              {items.map((l) => <Change key={l.id} l={l} />)}
            </section>
          );
        })}
      </>}
      {(!page.data || page.hasNextPage) && <Panels count={page.data ? 2 : 4} />}
      <div ref={page.end} aria-hidden="true" />
    </div>
  );
}

function Change({ l }: { l: LearningItem }) {
  const t = useTranslations("review");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [note, setNote] = useState<string | null>(null);
  const done = (data: ReviewPage) => {
    setNote(null);
    cache.setQueryData(["review"], data);
    for (const key of ["learnings", "saga", "game", "home"]) cache.invalidateQueries({ queryKey: [key] });
  };
  const undo = useMutation({ mutationFn: () => request<ReviewPage>(`/api/learnings/${l.id}/undo`, { method: "POST" }), onSuccess: done });
  const correct = useMutation({
    mutationFn: () => request<ReviewPage>(`/api/learnings/${l.id}/correct`, { method: "POST", body: JSON.stringify({ note }) }),
    onSuccess: done,
  });
  const list = (names: string[]) => names.map((n) => n || t("loose")).join(", ");
  const what = l.change === "alias" ? t("aliased", { text: l.nameBefore ?? "", game: l.nameAfter ?? "?" })
    : l.change === "move"
    ? t("moved", { games: list(l.games), from: list(l.from), to: l.toKind === "new" ? t("toNew", { name: l.to ?? "" }) : l.toKind === "none" ? t("toNone") : l.to ?? "" })
    : l.change === "merge" ? t("merged", { games: list(l.games), kept: l.kept ?? "" })
      : l.change === "rename" ? t("renamed", { before: l.nameBefore ?? "?", after: l.nameAfter ?? "" })
        : t(`learning.${l.change as "move"}`);
  return (
    <div className={`change-row${l.undone ? " is-undone" : ""}`}>
      <details>
        <summary>
          <p className="what"><b>{what}</b></p>
          <p className="small muted">{t(`learning.${l.change as "move"}`)} · {l.undone ? t("history.undoneByYou") : t("history.byClaude")}</p>
        </summary>
        {l.reason && <p className="small" style={{ marginTop: 6 }}>{l.reason}</p>}
        {l.rules.map((r, i) => <p key={i} className="small muted" style={{ marginTop: 4 }}>{t("rule", { text: r })}</p>)}
      </details>
      <div className="acc">
        {l.undone ? <span className="ot-badge">{t("undone")}</span> : <>
          {l.sagaId != null && <Link className="ot-btn ot-btn-ghost ot-btn-sm" href={`/saga/?id=${l.sagaId}`}>{t("openSaga")}</Link>}
          {l.change !== "alias" && <button className="ot-btn ot-btn-sm" type="button" onClick={() => setNote(note === null ? "" : null)}>{t("correct")}</button>}
          <button className="ot-btn ot-btn-sm" type="button" disabled={undo.isPending} onClick={() => undo.mutate()}>{t("undo")}</button>
        </>}
      </div>
      {note !== null && (
        <form onSubmit={(e) => { e.preventDefault(); correct.mutate(); }}>
          <textarea rows={2} maxLength={500} aria-label={t("correct")} placeholder={t("correctPlaceholder")} value={note} onChange={(e) => setNote(e.target.value)} />
          <div className="hero-actions">
            <button className="ot-btn ot-btn-primary ot-btn-sm" type="submit" disabled={correct.isPending || note.trim() === ""}>{t("send")}</button>
            <button className="ot-btn ot-btn-ghost ot-btn-sm" type="button" onClick={() => setNote(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      {(undo.error || correct.error) && <p className="error small" role="alert" style={{ gridColumn: "1 / -1" }}>{describe(undo.error ?? correct.error)}</p>}
    </div>
  );
}
