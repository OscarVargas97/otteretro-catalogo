"use client";

import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useFormatter } from "next-intl";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError, useDynamicT } from "@/lib/i18n";
import type { TaskInfo, TaskLogEntry } from "@/lib/types";

/** Tareas de la cola (F1.2): lo pendiente y lo reciente; cada cambio llega por el evento "task". */
export const useTasks = () => useQuery({ queryKey: ["tasks"], queryFn: () => request<TaskInfo[]>("/api/tasks") }).data ?? [];

// El orden del panel (F2.11), el mismo que da la API: lo que corre, la cola y lo terminado por hora de término.
const rank = (t: TaskInfo) => (t.status === "running" ? 0 : t.status === "queued" ? 1 : t.status === "paused" ? 2 : 3);
const byPanelOrder = (a: TaskInfo, b: TaskInfo) =>
  rank(a) - rank(b) || (b.finishedAt ?? "").localeCompare(a.finishedAt ?? "") || Number(a.id) - Number(b.id);

/** Pone al día la lista con una tarea que llegó por el evento "task". */
export function upsertTask(cache: QueryClient, task: TaskInfo) {
  cache.setQueryData<TaskInfo[]>(["tasks"], (list = []) => [task, ...list.filter((t) => t.id !== task.id)].sort(byPanelOrder));
  // Mover o migrar una biblioteca cambia sus rutas, su estado y lo que hay en Herramientas.
  if (task.kind.startsWith("library.") && task.status !== "running" && task.status !== "queued" && task.status !== "paused") {
    cache.invalidateQueries({ queryKey: ["libraries"] });
    cache.invalidateQueries({ queryKey: ["tools"] });
    cache.invalidateQueries({ queryKey: ["consoles", "detail"] });
  }
  // Un escaneo terminado cambia lo que tienes (#178).
  if ((task.kind === "library.scan" || task.kind === "library.verify" || task.kind === "library.import" || task.kind === "library.convert") && task.status === "done") {
    cache.invalidateQueries({ queryKey: ["game"] });
    cache.invalidateQueries({ queryKey: ["collection"] });
    cache.invalidateQueries({ queryKey: ["consoles"] });
  }
}

// Color de la insignia de cada estado (#172).
const BADGE: Record<string, string> = { queued: "", running: " ot-badge-info", paused: " ot-badge-review", done: " ot-badge-have", error: " ot-badge-danger", canceled: "" };
const FINISHED = ["done", "error", "canceled"];

/** Filas del panel de tareas (TaskRow del sistema de diseño, #172): barra de progreso, estado como insignia de color y ✕ para
 *  quitar una terminada. Al hacer clic se abre su registro (F2.10). */
export function TaskRows() {
  const t = useDynamicT();
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [open, setOpen] = useState<number | null>(null);
  const remove = useMutation({
    mutationFn: (id: number) => request<number>(`/api/tasks/${id}`, { method: "DELETE" }),
    onMutate: (id) => cache.setQueryData<TaskInfo[]>(["tasks"], (list = []) => list.filter((x) => Number(x.id) !== id)),
    onSettled: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });
  // Pausar, reanudar y cancelar (#146). Cancelar pide confirmar en la misma fila.
  const [confirm, setConfirm] = useState<number | null>(null);
  const act = useMutation({
    mutationFn: ({ id, action }: { id: number; action: "pause" | "resume" | "cancel" }) => request<void>(`/api/tasks/${id}/${action}`, { method: "POST" }),
    onSettled: () => { setConfirm(null); cache.invalidateQueries({ queryKey: ["tasks"] }); },
  });
  // «Probar ahora» en la tarea que espera el límite de Claude (#117).
  const retry = useMutation({
    mutationFn: () => request<unknown>("/api/claude/retry", { method: "POST" }),
    onSettled: () => { cache.invalidateQueries({ queryKey: ["tasks"] }); cache.invalidateQueries({ queryKey: ["claude"] }); },
  });
  return useTasks().map((task) => {
    const kind = `tasks.kind.${task.kind}`;
    const finished = FINISHED.includes(task.status);
    const progress = task.status === "done" ? 100 : Number(task.progress);
    const id = Number(task.id);
    const name = t.has(kind) ? t(kind) : task.kind;
    return (
      <div key={task.id} className={`ot-task${task.status === "error" ? " is-error" : ""}`}>
        <button type="button" className="ot-task-title" aria-expanded={open === id} onClick={() => setOpen(open === id ? null : id)}
          style={{ all: "unset", cursor: "pointer", fontWeight: "inherit" }}>
          {name} <span className="small muted">#{id}</span>
        </button>
        <span className="ot-task-end">
          <span className={`ot-badge${BADGE[task.status] ?? ""}`}>{t(`tasks.status.${task.status}`, { progress })}</span>
          {finished && <button type="button" className="task-remove" aria-label={t("tasks.remove", { name })} onClick={() => remove.mutate(id)}>✕</button>}
        </span>
        {!finished && (
          <span className="task-actions">
            {confirm === id ? (
              <>
                <span className="small">{t("tasks.cancelConfirm")}</span>
                <button type="button" className="ot-btn ot-btn-sm" onClick={() => setConfirm(null)}>{t("tasks.keep")}</button>
                <button type="button" className="ot-btn ot-btn-sm ot-btn-danger" disabled={act.isPending} onClick={() => act.mutate({ id, action: "cancel" })}>{t("tasks.cancel")}</button>
              </>
            ) : (
              <>
                {task.status === "paused"
                  ? <button type="button" className="ot-btn ot-btn-sm" disabled={act.isPending} onClick={() => act.mutate({ id, action: "resume" })}>{t("tasks.resume")}</button>
                  : <button type="button" className="ot-btn ot-btn-sm" disabled={act.isPending} onClick={() => act.mutate({ id, action: "pause" })}>{t("tasks.pause")}</button>}
                {task.status === "queued" && task.detail?.code === "tasks.claude.limit" &&
                  <button type="button" className="ot-btn ot-btn-sm" disabled={retry.isPending} onClick={() => retry.mutate()}>{t("tasks.retryLimit")}</button>}
                <button type="button" className="ot-btn ot-btn-sm ot-btn-ghost" onClick={() => setConfirm(id)}>{t("tasks.cancel")}</button>
              </>
            )}
          </span>
        )}
        <div className={`ot-progress${task.status === "error" ? " is-error" : ""}`}><span style={{ width: `${progress}%` }} /></div>
        {task.detail && <span className="ot-task-detail">{describe(task.detail)}</span>}
        {retry.error && task.detail?.code === "tasks.claude.limit" && <span className="error small">{describe(retry.error)}</span>}
        {open === id && <TaskLog id={id} stamp={`${task.status}|${task.progress}|${JSON.stringify(task.detail)}`} />}
      </div>
    );
  });
}

/** El registro de una tarea, lo más nuevo arriba; mientras corre se pone al día solo. */
/** El registro se vuelve a pedir cuando cambia la tarea (llega por SSE, evento `task`): sin sondeos. */
function TaskLog({ id, stamp }: { id: number; stamp: string }) {
  const t = useDynamicT();
  const describe = useDescribeError();
  const format = useFormatter();
  const log = useQuery({ queryKey: ["tasks", id, "log", stamp], queryFn: () => request<TaskLogEntry[]>(`/api/tasks/${id}/log`), placeholderData: (previous) => previous });
  if (!log.data) return null;
  if (log.data.length === 0) return <p className="small muted" style={{ margin: 0 }}>{t("tasks.logEmpty")}</p>;
  return (
    <ol className="small" aria-label={t("tasks.log")} style={{ margin: 0, paddingLeft: 0, listStyle: "none", maxHeight: 240, overflowY: "auto" }}>
      {log.data.map((l) => (
        <li key={l.id} className={l.level === "error" ? "error" : l.level === "warn" ? "muted" : undefined}>
          <span className="muted">{format.dateTime(new Date(l.at), { timeStyle: "medium" })}</span> {describe(l.message)}
        </li>
      ))}
    </ol>
  );
}

/** «Limpiar terminadas» (F2.11): borra las tareas hechas o con error y su registro. */
export function ClearTasks() {
  const t = useDynamicT();
  const cache = useQueryClient();
  const tasks = useTasks();
  const clear = useMutation({
    mutationFn: () => request<number>("/api/tasks/finished", { method: "DELETE" }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });
  if (!tasks.some((x) => FINISHED.includes(x.status))) return null;
  return <button className="ot-btn" type="button" disabled={clear.isPending} onClick={() => clear.mutate()}>{t("tasks.clear")}</button>;
}
