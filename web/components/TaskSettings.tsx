"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { optimistic } from "@/lib/optimistic";
import Link from "next/link";
import type { AutomationSuggestion, CatalogTaskInfo, ClaudeSettingsRequest, ClaudeStatus, ConsolesPage, CoversInfo, PortInfo, PortsPage, UsagePage } from "@/lib/types";
import { useSearch } from "./SagaEditing";

/** Los prompts de cada tarea de Claude, para filtrar el registro de uso (#58). */
const PROMPTS: Record<string, string> = { portSearch: "verificar-port", emulators: "verificar-fork", cleanup: "depurar-sagas", routes: "ruta-saga", iconic: "iconico", best: "mejor-version", translate: "traducir-descripcion" };

/**
 * Ajustes › Tareas (#58, decisiones 283 a 285; antes «Claude», #110): el horario común, cada tarea del catálogo en dos grupos
 * (con Claude y sin Claude) con qué hace, su interruptor del horario y «Ejecutar», y abajo el registro de uso plegado.
 */
export function TaskSettings() {
  const t = useTranslations("settings.claude");
  const describe = useDescribeError();
  const format = useFormatter();
  const cache = useQueryClient();
  const status = useQuery({ queryKey: ["claude"], queryFn: () => request<ClaudeStatus>("/api/claude"), refetchInterval: (q) => (q.state.data?.task ? 5_000 : 30_000) });
  const [share, setShare] = useState<number | null>(null);
  const done = (data: ClaudeStatus) => cache.setQueryData(["claude"], data);
  // Cada cambio se ve al tocar y se guarda solo (#107, #110).
  const save = useMutation({
    mutationFn: (body: Partial<ClaudeSettingsRequest>) => request<ClaudeStatus>("/api/claude", { method: "PUT", body: JSON.stringify(body) }),
    ...optimistic(cache, ["claude"], (d: ClaudeStatus, body: Partial<ClaudeSettingsRequest>) =>
      ({ ...d, ...Object.fromEntries(Object.entries(body).filter(([, v]) => v != null)) })),
    onSuccess: done,
  });
  // «Probar ahora» (#117): si el límite ya se reinició, sale de la pausa y las tareas siguen.
  const retry = useMutation({
    mutationFn: () => request<ClaudeStatus>("/api/claude/retry", { method: "POST" }),
    onSuccess: (data) => { done(data); cache.invalidateQueries({ queryKey: ["tasks"] }); },
  });

  if (!status.data) return status.isError ? <p className="error" role="alert">{describe(status.error)}</p> : null;
  const s = status.data;
  const at = (iso: string) => format.dateTime(new Date(iso), { weekday: "long", hour: "2-digit", minute: "2-digit" });
  const task = s.task;
  const commitShare = () => {
    if (share != null && share >= 10 && share <= 100 && share !== Number(s.windowShare)) save.mutate({ windowShare: share });
    setShare(null);
  };

  return (
    <div className="settings-blocks">
      <section className="panel">
        <h2>{t("schedule")}</h2>
        <div className="sentence">
          <span>{t("runsFrom")}</span>
          <input type="time" aria-label={t("from")} value={s.scheduleStart ?? ""} onChange={(e) => e.target.value && save.mutate({ scheduleStart: e.target.value })} />
          <span>{t("runsTo")}</span>
          <input type="time" aria-label={t("to")} value={s.scheduleEnd ?? ""} onChange={(e) => e.target.value && save.mutate({ scheduleEnd: e.target.value })} />
        </div>
        <div className="sentence">
          <label className="sentence">
            <input type="checkbox" role="switch" checked={s.capped} onChange={(e) => save.mutate({ capped: e.target.checked })} />
            {s.capped ? t("dayCap") : t("dayCapOff")}
          </label>
          {s.capped && (
            <>
              <input type="number" min={10} max={100} style={{ width: 72 }} aria-label={t("share")} value={share ?? Number(s.windowShare)}
                onChange={(e) => setShare(Number(e.target.value))} onBlur={commitShare} onKeyDown={(e) => { if (e.key === "Enter") commitShare(); }} />
              <span>{t("dayCapEnd")}</span>
            </>
          )}
        </div>
        {save.error && <p className="error small" role="alert">{describe(save.error)}</p>}
      </section>

      <section className="panel">
        <h2>{t("now")}</h2>
        <p className="small" role="status" style={{ margin: 0 }}>
          {s.pausedUntil ? t("paused", { at: at(s.pausedUntil) })
            : s.runNowUntil ? t("runNowUntil", { at: at(s.runNowUntil) })
              : s.inSchedule ? t("inSchedule") : t("outSchedule", { start: s.scheduleStart })}
          {task && <> {task.status === "running" ? t("taskRunning", { id: Number(task.id), progress: Number(task.progress) })
            : task.resumeAt ? t("taskWaiting", { id: Number(task.id), at: at(task.resumeAt) })
              : t("taskQueued", { id: Number(task.id) })}</>}
        </p>
        {s.pausedUntil && <button className="ot-btn ot-btn-sm" type="button" disabled={retry.isPending} onClick={() => retry.mutate()}>{t("retry")}</button>}
        {retry.isSuccess && <p className="small" role="status" style={{ margin: 0 }}>{retry.data.pausedUntil ? t("stillLimited") : t("limitReset")}</p>}
        {retry.error && <p className="error small" role="alert">{describe(retry.error)}</p>}
      </section>

      <RunAll />
      <TaskGroup claude />
      <TaskGroup claude={false} />

      {/* El registro completo, plegado y con filtro por tarea (decisión 285). */}
      <details className="panel">
        <summary><h2 style={{ display: "inline" }}>{t("use")}</h2></summary>
        <UsageLog stamp={status.dataUpdatedAt} />
        <AutomationSuggestions />
      </details>
    </div>
  );
}

/** Registro de uso de a 10 (#95), compacto (#110): «ok» no se dice y un error va en rojo con su motivo. */
function UsageLog({ stamp }: { stamp: number }) {
  const t = useTranslations("settings.claude");
  const format = useFormatter();
  const [page, setPage] = useState(1);
  const [prompt, setPrompt] = useState("");
  const usage = useQuery({
    queryKey: ["claude", "usage", page, prompt, stamp],
    queryFn: () => request<UsagePage>(`/api/claude/usage?page=${page}${prompt ? `&prompt=${prompt}` : ""}`), placeholderData: (previous) => previous,
  });
  const u = usage.data;
  if (!u) return null;
  const filter = (
    <select aria-label={t("usageFilter")} value={prompt} onChange={(e) => { setPrompt(e.target.value); setPage(1); }}>
      <option value="">{t("usageAll")}</option>
      {Object.entries(PROMPTS).map(([id, p]) => <option key={id} value={p}>{t(`task.${id as "routes"}.name`)}</option>)}
    </select>
  );
  const at = Number(u.page), pages = Number(u.pages);
  const num = (n: number | string) => format.number(Number(n));
  return (
    <>
      {filter}
      {u.rows.length > 0 && (
        // El total de la página, arriba del registro (#172).
        <p className="small usage-total" style={{ margin: 0 }}>
          {t("pageTotal", {
            calls: u.rows.length,
            input: num(u.rows.reduce((a, r) => a + Number(r.inputTokens), 0)),
            output: num(u.rows.reduce((a, r) => a + Number(r.outputTokens), 0)),
            cost: format.number(u.rows.reduce((a, r) => a + Number(r.costUsd), 0), { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 3 }),
          })}
        </p>
      )}
      {u.rows.length === 0 ? <p className="small muted" style={{ margin: 0 }}>{t("noUsage")}</p> : (
        <div className="table-wrap">
          <table className="t">
            <thead><tr><th>{t("when")}</th><th>{t("prompt")}</th><th>{t("model")}</th><th className="num">{t("duration")}</th><th className="num">{t("input")}</th><th className="num">{t("output")}</th><th className="num">{t("cost")}</th></tr></thead>
            <tbody>
              {u.rows.map((r, i) => (
                <tr key={i}>
                  <td className="small" title={format.dateTime(new Date(r.createdAt), { dateStyle: "medium", timeStyle: "short" })}>{format.relativeTime(new Date(r.createdAt), new Date())}</td>
                  <td>{r.prompt}{r.result !== "ok" && <> <span className="error small">{t(`resultCode.${r.result as "failed"}`)}</span></>}</td>
                  <td>{r.model ? <span className="ot-badge">{r.model}</span> : "—"}</td>
                  <td className="num">{Math.round(Number(r.durationMs) / 1000)} s</td>
                  <td className="num">{num(r.inputTokens)}</td>
                  <td className="num">{num(r.outputTokens)}</td>
                  <td className="num">{format.number(Number(r.costUsd), { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 3 })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {pages > 1 && (
        <div className="chips" style={{ alignItems: "center" }}>
          <button className="ot-btn ot-btn-sm" type="button" disabled={at <= 1} onClick={() => setPage(at - 1)}>{t("previous")}</button>
          <span className="small muted">{t("pageOf", { page: at, pages })}</span>
          <button className="ot-btn ot-btn-sm" type="button" disabled={at >= pages} onClick={() => setPage(at + 1)}>{t("next")}</button>
        </div>
      )}
    </>
  );
}

/** Lo que Claude repite y conviene automatizar (§11.5): «Crear issue» lo deja en GitHub con la etiqueta automatizar. */
function AutomationSuggestions() {
  const t = useTranslations("settings.claude");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const list = useQuery({ queryKey: ["claude", "automation"], queryFn: () => request<AutomationSuggestion[]>("/api/claude/automation") });
  const create = useMutation({
    mutationFn: (key: string) => request<AutomationSuggestion[]>(`/api/claude/automation/${key}/issue`, { method: "POST" }),
    onSuccess: (data) => cache.setQueryData(["claude", "automation"], data),
  });
  return (
    <>
      <h3 className="small muted console-group">{t("automation")}</h3>
      {list.data?.length === 0 && <p className="small muted" style={{ margin: 0 }}>{t("noAutomation", { count: 10 })}</p>}
      {list.data?.map((a) => (
        <div key={a.key} className="chips" style={{ alignItems: "center" }}>
          <span className="small"><b>{t("automationTimes", { count: Number(a.count) })}</b> · {a.change} · {a.reason}</span>
          {a.issueUrl
            ? <a className="small" href={a.issueUrl} target="_blank" rel="noreferrer">{t("issueCreated")}</a>
            : <button className="ot-btn ot-btn-sm" type="button" disabled={create.isPending} onClick={() => create.mutate(a.key)}>{t("createIssue")}</button>}
        </div>
      ))}
      {create.error && <p className="error small" role="alert">{describe(create.error)}</p>}
    </>
  );
}

const IDS = { claude: ["portSearch", "emulators", "cleanup", "iconic", "routes", "best", "translate"], script: ["igdb", "codes", "ports", "images"] } as const;

const ACTIVE = ["queued", "running", "paused"];

/** La lista de tareas, liviana (#83): se refresca cada 5 s si algo corre y cada 30 s si no. */
function useCatalogTasks() {
  return useQuery({
    queryKey: ["catalog-tasks"], queryFn: () => request<CatalogTaskInfo[]>("/api/catalog-tasks"),
    refetchInterval: (q) => (q.state.data?.some((x) => x.lastStatus === "queued" || x.lastStatus === "running") ? 5_000 : 30_000),
  });
}

/**
 * Cuánto le falta a cada tarea (#83): recorre el catálogo, así que va aparte de la lista y fuera de su refresco. Se pide al abrir
 * la sección y otra vez cuando una tarea deja la cola (terminó, falló o se canceló).
 */
function usePendingCounts(tasks: CatalogTaskInfo[] | undefined) {
  const counts = useQuery({ queryKey: ["catalog-tasks-pending"], queryFn: () => request<Record<string, number>>("/api/catalog-tasks/pending") });
  const active = tasks?.filter((x) => ACTIVE.includes(x.lastStatus ?? "")).map((x) => x.id).join(",");
  const before = useRef(active);
  const { refetch } = counts;
  useEffect(() => {
    const left = before.current?.split(",").some((id) => id && !active?.split(",").includes(id));
    before.current = active;
    if (left) refetch();
  }, [active, refetch]);
  return counts;
}

/** «Ejecutar todo en orden» (#161): encola ahora la secuencia de la noche; la cola las corre de a una. */
function RunAll() {
  const t = useTranslations("settings.claude");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const run = useMutation({
    mutationFn: () => request<CatalogTaskInfo[]>("/api/catalog-tasks/run", { method: "POST" }),
    onSuccess: (data) => {
      cache.setQueryData(["catalog-tasks"], data);
      cache.invalidateQueries({ queryKey: ["tasks"] });
      cache.invalidateQueries({ queryKey: ["claude"] });
    },
  });
  return (
    <section className="panel">
      <div className="chips" style={{ alignItems: "center" }}>
        <button className="ot-btn ot-btn-sm" type="button" disabled={run.isPending} onClick={() => run.mutate()}>{t("runAll")}</button>
        <span className="small muted">{t("runAllText")}</span>
      </div>
      {run.error && <p className="error small" role="alert">{describe(run.error)}</p>}
    </section>
  );
}

/** Un grupo de tareas (decisión 283): con Claude o sin Claude, en el orden en que corren de noche. */
function TaskGroup({ claude }: { claude: boolean }) {
  const t = useTranslations("settings.claude");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const tasks = useCatalogTasks();
  const counts = usePendingCounts(tasks.data);
  const done = (data: CatalogTaskInfo[]) => {
    cache.setQueryData(["catalog-tasks"], data);
    cache.invalidateQueries({ queryKey: ["tasks"] });
    cache.invalidateQueries({ queryKey: ["claude"] });
  };
  type Change = { id: string; auto?: boolean; days?: number };
  const save = useMutation({
    mutationFn: ({ id, ...body }: Change) => request<CatalogTaskInfo[]>(`/api/catalog-tasks/${id}`, { method: "PUT", body: JSON.stringify(body) }),
    ...optimistic(cache, ["catalog-tasks"], (d: CatalogTaskInfo[], v: Change) =>
      d.map((x) => (x.id === v.id ? { ...x, ...(v.auto != null && { auto: v.auto }), ...(v.days != null && { days: v.days }) } : x))),
    onSuccess: done,
  });
  const run = useMutation({
    mutationFn: ({ id, console }: { id: string; console?: string }) =>
      request<CatalogTaskInfo[]>(`/api/catalog-tasks/${id}/run${console ? `?console=${encodeURIComponent(console)}` : ""}`, { method: "POST" }),
    onSuccess: done,
  });
  const ids: readonly string[] = claude ? IDS.claude : IDS.script;
  const list = ids.map((id) => tasks.data?.find((x) => x.id === id)).filter((x): x is CatalogTaskInfo => !!x);
  const error = save.error ?? run.error;
  return (
    <section className="panel">
      <h2>{t(claude ? "groupClaude" : "groupScript")}</h2>
      {tasks.isError && <p className="error small" role="alert">{describe(tasks.error)}</p>}
      {list.map((task) => (
        <TaskRow key={task.id} task={task} pendingCount={counts.data?.[task.id]} busy={run.isPending}
          onAuto={(auto) => save.mutate({ id: task.id, auto })} onDays={(days) => save.mutate({ id: task.id, days })}
          onRun={(console) => run.mutate({ id: task.id, console })} />
      ))}
      {error && <p className="error small" role="alert">{describe(error)}</p>}
    </section>
  );
}

/** Una tarea: qué hace, si corre sola en el horario, su última ejecución, lo del mes y «Ejecutar». */
function TaskRow({ task, pendingCount, busy, onAuto, onDays, onRun }: {
  task: CatalogTaskInfo; pendingCount?: number | string; busy: boolean; onAuto: (auto: boolean) => void; onDays: (days: number) => void; onRun: (console?: string) => void;
}) {
  const t = useTranslations("settings.claude");
  const ts = useTranslations("tasks.status");
  const format = useFormatter();
  const [days, setDays] = useState<number | null>(null);
  const [platform, setPlatform] = useState("");
  const id = task.id as "routes";
  const consoles = useQuery({ queryKey: ["consoles", "settings"], queryFn: () => request<ConsolesPage>("/api/consoles/settings"), enabled: task.id === "iconic" });
  const covers = useQuery({ queryKey: ["covers"], queryFn: () => request<CoversInfo>("/api/covers"), enabled: task.id === "images" });
  const status = (task.lastStatus ?? null) as "queued" | null;
  const pending = ACTIVE.includes(task.lastStatus ?? "");
  const ago = task.lastAt ? format.relativeTime(new Date(task.lastAt), new Date()) : "";
  // Cuánto le queda (#76): al día, N pendientes o, en IGDB, cuándo le toca según su intervalo.
  const due = task.dueAt ? Math.ceil((new Date(task.dueAt).getTime() - Date.now()) / 86_400_000) : 0;
  const left = pendingCount != null
    ? (Number(pendingCount) > 0 ? t(`pending.${id as "translate"}`, { count: Number(pendingCount) }) : t("upToDate"))
    : task.days != null ? (due > 0 ? t("dueIn", { days: due }) : t("dueToday")) : null;
  // «Ports» suma sus recetas propuestas que esperan en Revisar (#119).
  const last = task.lastStatus == null || !task.lastAt ? t("never")
    : pending ? t("last", { status: ts(status!, { progress: "…" }), ago })
      : t(`lastRun.${(task.lastStatus === "done" ? "done" : task.lastStatus === "error" ? "error" : "canceled")}`, { ago });
  const commitDays = () => { if (days != null && days >= 1 && days !== Number(task.days)) onDays(days); setDays(null); };
  const money = (n: number | string) => format.number(Number(n), { style: "currency", currency: "USD", maximumFractionDigits: 2 });
  return (
    <div className="version-row" style={{ alignItems: "flex-start" }}>
      <details style={{ flex: 1 }}>
        <summary>
          <strong>{t(`task.${id}.name`)}</strong> {task.model && <span className="ot-badge">{task.model}</span>}
          {left && <span className="small"> · {left}</span>}
          <span className={`small ${task.lastStatus === "error" ? "error" : "muted"}`}> · {last}</span>
        </summary>
        <p className="small muted" style={{ margin: "2px 0" }}>{t(`task.${id}.text`)}</p>
        <div className="sentence small">
          <label className="sentence">
            <input type="checkbox" role="switch" checked={task.auto} onChange={(e) => onAuto(e.target.checked)} />
            {t("auto")}
          </label>
          {task.days != null && (
            <>
              <span>· {t("every")}</span>
              <input type="number" min={1} max={365} style={{ width: 64 }} aria-label={t("everyDays")} value={days ?? Number(task.days)}
                onChange={(e) => setDays(Number(e.target.value))} onBlur={commitDays} onKeyDown={(e) => { if (e.key === "Enter") commitDays(); }} />
              <span>{t("days")}</span>
            </>
          )}
        </div>
        <p className="small muted" style={{ margin: 0 }}>
          {task.model ? t("month", { calls: Number(task.monthCalls), cost: money(task.monthCost) }) : null}
          {task.id === "images" && covers.data && <> · {t("covers", { have: Number(covers.data.have), total: Number(covers.data.total) })}</>}
        </p>
        {task.id === "ports" && <UnmatchedPorts />}
      </details>
      <div className="sentence">
        {task.id === "iconic" && (
          // «Reevaluar sagas icónicas» (F2.15): de una consola, olvida lo dicho y lo vuelve a pedir.
          <select aria-label={t("iconicScope")} value={platform} onChange={(e) => setPlatform(e.target.value)}>
            <option value="">{t("iconicAll")}</option>
            {consoles.data?.consoles.filter((c) => Number(c.games) > 0).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        )}
        {task.id === "images" && covers.data && !covers.data.hasKey
          ? <Link className="ot-btn" href="/settings/?s=credentials">{t("noKey")}</Link>
          : <button className="ot-btn" type="button" disabled={busy || pending} onClick={() => onRun(platform || undefined)}>
              {pending && status ? ts(status, { progress: "…" }) : t("run")}</button>}
      </div>
    </div>
  );
}

/** Los ports que no cruzaron con un juego (#69, decisión 300): quedan en la lista y se asignan a mano. */
function UnmatchedPorts() {
  const t = useTranslations("settings.claude.ports");
  const ports = useQuery({ queryKey: ["ports"], queryFn: () => request<PortsPage>("/api/ports") });
  const p = ports.data;
  if (!p || Number(p.total) === 0) return null;
  return (
    <details>
      <summary className="small muted">{t("summary", { total: Number(p.total), matched: Number(p.matched), unmatched: p.unmatched.length })}</summary>
      {/* «100 % jugable» va aparte de lo jugable con fallos (pedido de la persona, #69). */}
      <div className="chips">
        {Object.entries(p.byStatus).map(([status, count]) => (
          <span key={status} className="ot-badge">{t(`status.${status as "complete"}`)}: {Number(count)}</span>
        ))}
      </div>
      {p.unmatched.map((port) => <UnmatchedPort key={port.id} port={port} />)}
    </details>
  );
}

/** Un port sin juego: buscar su juego y asignarlo. Lo asignado a mano el cruce no lo cambia. */
function UnmatchedPort({ port }: { port: PortInfo }) {
  const t = useTranslations("settings.claude.ports");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [text, setText] = useState("");
  const search = useSearch(text);
  const assign = useMutation({
    mutationFn: (gameId: number | string) => request<PortsPage>(`/api/ports/${port.id}`, { method: "PUT", body: JSON.stringify({ gameId: Number(gameId) }) }),
    onSuccess: (data) => cache.setQueryData(["ports"], data),
  });
  return (
    <div className="field-editor">
      <span className="small"><b>{port.game}</b> · {port.console} · <a href={port.link} target="_blank" rel="noreferrer">{port.project}</a> · <span className="ot-badge">{t(`status.${port.status as "complete"}`)}</span></span>
      <input aria-label={t("search")} placeholder={t("search")} value={text} onChange={(e) => setText(e.target.value)} />
      <div className="chips">
        {search.data?.games.map((g) => (
          <button key={g.id} className="chip" type="button" disabled={assign.isPending} onClick={() => assign.mutate(g.id)}>{t("assign", { name: g.name })}</button>
        ))}
      </div>
      {assign.error && <p className="error small" role="alert">{describe(assign.error)}</p>}
    </div>
  );
}
