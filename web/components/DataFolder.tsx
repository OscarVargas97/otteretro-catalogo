"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError, useDynamicT } from "@/lib/i18n";
import type { AppState, FolderRequest, MoveState } from "@/lib/types";

/** Estado de la mudanza de la carpeta de datos; llega en vivo por el evento "move". */
export const useMove = () =>
  useQuery({ queryKey: ["move"], queryFn: () => request<MoveState>("/api/data-folder/move") }).data;

const percent = (m: MoveState) => (Number(m.total) > 0 ? Math.round((Number(m.done) / Number(m.total)) * 100) : 0);

/** Nombre del paso en el idioma activo; el backend manda su código (move.step.*). */
function useStepName() {
  const t = useDynamicT();
  return (step: string) => (t.has(`move.step.${step}`) ? t(`move.step.${step}`) : step);
}

/** Fila del panel de tareas mientras se mueve la carpeta de datos (o con su resultado). */
export function MoveTask() {
  const t = useTranslations("dataFolder");
  const describe = useDescribeError();
  const stepName = useStepName();
  const m = useMove();
  if (!m || !(m.running || m.step === "done" || m.step === "error")) return null;
  const destination = m.destination ?? "";
  return (
    <div className={`ot-task${m.error ? " is-error" : ""}`}>
      <span className="ot-task-title">{t("taskTitle")}</span>
      <span className="ot-task-state">{m.running ? `${percent(m)} %` : m.error ? t("error") : t("taskDone")}</span>
      {m.running && <div className="ot-progress"><span style={{ width: `${percent(m)}%` }} /></div>}
      <span className="ot-task-detail">
        {m.error ? describe(m.error) : m.running ? t("progress", { step: stepName(m.step), destination }) : t("taskNowIn", { destination })}
      </span>
    </div>
  );
}

/** Llevar los datos de la app a otro disco (#169): es la fila «Datos de Otteretro» de Almacenamiento. */
export function useMoveData() {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: (body: FolderRequest) => request<MoveState>("/api/data-folder/move", { method: "POST", body: JSON.stringify(body) }),
    // El aviso de fin por SSE puede llegar antes que esta respuesta: no se pisa con el «empezó».
    onSuccess: (state) => cache.setQueryData<MoveState>(["move"], (old) => (old && !old.running && old.destination === state.destination ? old : state)),
  });
}

/** Progreso y resultado de la mudanza de los datos; al terminar, la ruta y el disco ★ pasan a ser los nuevos. */
export function DataMoveStatus() {
  const t = useTranslations("dataFolder");
  const describe = useDescribeError();
  const stepName = useStepName();
  const cache = useQueryClient();
  const m = useMove();
  const [dismissed, setDismissed] = useState<string | null>(null); // el aviso se cierra (#111)

  const finishedAt = m?.step === "done" ? m.destination : null;
  useEffect(() => {
    if (finishedAt) for (const key of ["state", "folder", "libraries"]) cache.invalidateQueries({ queryKey: [key] });
  }, [finishedAt, cache]);

  if (!m) return null;
  const destination = m.destination ?? "";
  const notice = !m.running && (m.step === "done" || m.error) ? `${m.step}|${m.destination}` : null;
  if (m.running) return (
    <div className="form-block" role="status">
      <div className="version-row">
        <span><span className="spinner" aria-hidden="true" style={{ display: "inline-block", marginRight: 8 }} />{t("progress", { step: stepName(m.step), destination })}</span>
        <span className="small muted">{percent(m)} %</span>
      </div>
      <div className="ot-progress"><span style={{ width: `${percent(m)}%` }} /></div>
      <span className="small muted">{t("dontClose")}</span>
    </div>
  );
  if (notice === null || notice === dismissed) return null;
  return (
    <div className="result" role="status">
      <span className={`ot-badge ${m.error ? "ot-badge-danger" : "ot-badge-have"}`}>{m.error ? t("error") : t("moved")}</span>
      <span className="small muted" style={{ flex: 1 }}>{m.error ? describe(m.error) : m.warning ? describe(m.warning) : t("nowIn", { destination })}</span>
      <button className="ot-btn ot-btn-sm ot-btn-ghost" type="button" aria-label={t("dismiss")} onClick={() => setDismissed(notice)}>✕</button>
    </div>
  );
}

/** «⋯ › Usar otra carpeta de datos» (S7): la de otro PC, por ejemplo. La actual no se toca. */
export function UseOtherFolder({ onDone }: { onDone: () => void }) {
  const t = useTranslations("dataFolder");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [path, setPath] = useState("");
  const use = useMutation({
    mutationFn: (body: FolderRequest) => request<AppState>("/api/data-folder", { method: "POST", body: JSON.stringify(body) }),
    onSuccess: () => {
      for (const key of ["state", "folder", "libraries"]) cache.invalidateQueries({ queryKey: [key] });
      onDone();
    },
  });
  return (
    <section className="settings-blocks">
      <h2 id="use-folder" style={{ margin: 0 }}>{t("useButton")}</h2>
      <p className="small" style={{ margin: 0 }}>{t("useHelp")}</p>
      <label className="small muted" htmlFor="new-path">{t("folderLabel")}</label>
      <input id="new-path" value={path} onChange={(e) => setPath(e.target.value)} placeholder={t("placeholder")} />
      {use.isError && <p className="error small" style={{ margin: 0 }}>{describe(use.error)}</p>}
      <div style={{ display: "flex", gap: 8 }}>
        <button className="ot-btn" type="button" onClick={onDone}>{t("cancel")}</button>
        <button className="ot-btn ot-btn-primary" type="button" disabled={!path || use.isPending} onClick={() => use.mutate({ path })}>{t("useThis")}</button>
      </div>
    </section>
  );
}
