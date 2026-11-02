"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { optimistic } from "@/lib/optimistic";
import type { AutomationSuggestion, ClaudeSettingsRequest, ClaudeStatus, ConsolesPage, UsagePage } from "@/lib/types";

/**
 * Ajustes › Claude (§10, #110) en tres bloques: Horario (en una frase, se guarda solo), Ahora (estado, «Ejecutar ahora» y
 * reevaluar icónicas) y Uso (registro y sugerencias de automatizar).
 */
export function ClaudeSettings() {
  const t = useTranslations("settings.claude");
  const describe = useDescribeError();
  const format = useFormatter();
  const cache = useQueryClient();
  const status = useQuery({ queryKey: ["claude"], queryFn: () => request<ClaudeStatus>("/api/claude"), refetchInterval: (q) => (q.state.data?.task ? 5_000 : 30_000) });
  const [share, setShare] = useState<number | null>(null);
  const [platform, setPlatform] = useState("");
  const consoles = useQuery({ queryKey: ["consoles", "settings"], queryFn: () => request<ConsolesPage>("/api/consoles/settings") });
  const iconic = useMutation({
    mutationFn: () => request<number>(`/api/claude/iconic${platform ? `?console=${encodeURIComponent(platform)}` : ""}`, { method: "POST" }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });
  const done = (data: ClaudeStatus) => cache.setQueryData(["claude"], data);
  // Cada cambio se ve al tocar y se guarda solo (#107, #110).
  const save = useMutation({
    mutationFn: (body: Partial<ClaudeSettingsRequest>) => request<ClaudeStatus>("/api/claude", { method: "PUT", body: JSON.stringify(body) }),
    ...optimistic(cache, ["claude"], (d: ClaudeStatus, body: Partial<ClaudeSettingsRequest>) =>
      ({ ...d, ...Object.fromEntries(Object.entries(body).filter(([, v]) => v != null)) })),
    onSuccess: done,
  });
  const runNow = useMutation({
    mutationFn: () => request<ClaudeStatus>("/api/claude/run-now", { method: "POST" }),
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
        <div className="sentence">
          <button className="ot-btn ot-btn-primary" type="button" disabled={runNow.isPending || task?.status === "running"} onClick={() => runNow.mutate()}>{t("runNow")}</button>
          <span style={{ flex: 1 }} />
          <select aria-label={t("iconicScope")} value={platform} onChange={(e) => setPlatform(e.target.value)}>
            <option value="">{t("iconicAll")}</option>
            {consoles.data?.consoles.filter((c) => Number(c.games) > 0).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
          <button className="ot-btn" type="button" disabled={iconic.isPending} onClick={() => iconic.mutate()}>{t("iconic")}</button>
        </div>
        {iconic.isSuccess && <span className="small muted">{t("iconicQueued")}</span>}
        {iconic.isError && <span className="small muted">{t("iconicNone")}</span>}
        {runNow.error && <p className="error small" role="alert">{describe(runNow.error)}</p>}
      </section>

      <section className="panel">
        <h2>{t("use")}</h2>
        <UsageLog stamp={status.dataUpdatedAt} />
        <AutomationSuggestions />
      </section>
    </div>
  );
}

/** Registro de uso de a 10 (#95), compacto (#110): «ok» no se dice y un error va en rojo con su motivo. */
function UsageLog({ stamp }: { stamp: number }) {
  const t = useTranslations("settings.claude");
  const format = useFormatter();
  const [page, setPage] = useState(1);
  const usage = useQuery({ queryKey: ["claude", "usage", page, stamp], queryFn: () => request<UsagePage>(`/api/claude/usage?page=${page}`), placeholderData: (previous) => previous });
  const u = usage.data;
  if (!u) return null;
  const at = Number(u.page), pages = Number(u.pages);
  const num = (n: number | string) => format.number(Number(n));
  return (
    <>
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
