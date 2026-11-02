"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError, useDynamicT } from "@/lib/i18n";
import type { UpdateChannelInfo, UpdateState } from "@/lib/types";

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Estado de la actualización de la app; llega en vivo por el evento "update". */
export const useUpdate = () =>
  useQuery({ queryKey: ["update"], queryFn: () => request<UpdateState>("/api/update") }).data;

/** Motivo que manda el backend como código (update.reason.*), o null. */
function useReason() {
  const t = useDynamicT();
  return (u?: UpdateState) => (u?.message && t.has(`update.reason.${u.message}`) ? t(`update.reason.${u.message}`) : null);
}

/** Fila del panel de tareas (TaskRow del sistema de diseño). No muestra nada si no hay actualización en marcha. */
export function UpdateTask() {
  const t = useTranslations("update");
  const reason = useReason();
  const u = useUpdate();
  if (!u || !["Checking", "Downloading", "Ready", "Error"].includes(u.phase)) return null;
  const progress = Number(u.progress);
  const version = u.version ?? "";
  const state: Record<string, string> = {
    Checking: t("checking"),
    Downloading: `${progress} %`,
    Ready: t("ready"),
    Error: t("error"),
  };
  const detail: Record<string, string> = {
    Checking: t("checkingDetail"),
    Downloading: t("downloadingDetail", { version }),
    Ready: t("readyDetail", { version }),
    Error: reason(u) ?? t("failedFallback"),
  };
  return (
    <div className={`ot-task${u.phase === "Error" ? " is-error" : ""}`}>
      <span className="ot-task-title">{t("taskTitle")}</span>
      <span className="ot-task-state">{state[u.phase]}</span>
      {u.phase === "Downloading" && (
        <div className="ot-progress"><span style={{ width: `${progress}%` }} /></div>
      )}
      <span className="ot-task-detail">{detail[u.phase]}</span>
    </div>
  );
}

/** Ajustes › Acerca de: versión actual, botón para buscar y el estado de la actualización en el mismo sitio. */
export function VersionAndUpdate({ version }: { version?: string }) {
  const t = useTranslations("update");
  const format = useFormatter();
  const reason = useReason();
  const cache = useQueryClient();
  const u = useUpdate();
  const check = useMutation({
    // Aunque la comprobación tarde milisegundos, el botón muestra la ruedita un instante para que se note.
    mutationFn: async () =>
      (await Promise.all([request<UpdateState>("/api/update/check", { method: "POST" }), wait(700)]))[0],
    // La descarga actualiza su progreso por eventos; no se pisa con la respuesta.
    onSuccess: (state) => state.phase !== "Downloading" && cache.setQueryData(["update"], state),
  });
  const restart = useMutation({ mutationFn: () => request("/api/update/restart", { method: "POST" }) });
  const busy = check.isPending || u?.phase === "Checking" || u?.phase === "Downloading";
  const time = u?.checkedAt ? format.dateTime(new Date(u.checkedAt), { hour: "2-digit", minute: "2-digit" }) : "";
  const newVersion = u?.version ?? "";
  const results: Record<string, [string, string, string]> = {
    // fase: [clase de la insignia, insignia, texto]
    Unavailable: u?.message === "withSystem"
      ? ["ot-badge ot-badge-info", t("withSystemBadge"), reason(u) ?? ""]
      : ["ot-badge ot-badge-review", t("unavailable"), reason(u) ?? ""],
    UpToDate: ["ot-badge ot-badge-have", t("upToDate"), t("upToDateText", { version: version ?? "", time })],
    Downloading: ["ot-badge ot-badge-info", t("downloadingBadge", { version: newVersion }), t("downloadingText", { progress: Number(u?.progress) })],
    Ready: ["ot-badge ot-badge-have", t("readyBadge", { version: newVersion }), t("readyText")],
    Error: ["ot-badge ot-badge-danger", t("error"), reason(u) ?? t("failedFallback")],
  };
  const [badgeClass, badge, text] = (!check.isPending && u && results[u.phase]) || [];
  // El botón solo muestra la ruedita: lo que está pasando se dice acá, también al lector de pantalla.
  const checking = busy && u?.phase !== "Downloading";
  return (
    <div className="panel">
      <div className="version-row">
        <span>Otteretro Catálogo {version}</span>
        {u?.phase === "Ready" ? (
          <button className="ot-btn ot-btn-primary" type="button" disabled={restart.isPending} aria-busy={restart.isPending} onClick={() => restart.mutate()}>
            {restart.isPending && <span className="spinner" aria-hidden="true" />}
            {t("restartNow")}
          </button>
        ) : u?.message === "withSystem" ? null : (
          <button className="ot-btn" type="button" disabled={busy} aria-busy={busy} onClick={() => check.mutate()}>
            {busy && <span className="spinner" aria-hidden="true" />}
            {t("check")}
          </button>
        )}
      </div>
      {/* La región de estado siempre está: los lectores de pantalla anuncian lo que cambia adentro, no una región nueva. */}
      <div role="status">
        {checking && (
          <div className="result">
            <span className="small muted">{t("checkingDetail")}</span>
          </div>
        )}
        {badge && (
          // key: cada comprobación nueva vuelve a animar el resultado, aunque sea el mismo.
          <div className="result" key={`${u?.phase}-${u?.checkedAt}`}>
            <span className={badgeClass}>{badge}</span>
            <span className="small muted">{text}</span>
          </div>
        )}
      </div>
      {u?.phase === "Downloading" && (
        <div className="ot-progress"><span style={{ width: `${Number(u.progress)}%` }} /></div>
      )}
    </div>
  );
}

const CHANNELS = ["stable", "testing", "local", "custom"] as const;

/**
 * Ajustes › General › Actualizaciones (#12): Estable (releases de main), Pruebas (prereleases de los PR) u Otro origen
 * (carpeta, URL o repo de GitHub de quien tiene su propia versión). Al cambiar, la app busca de inmediato.
 */
export function UpdateChannel() {
  const t = useTranslations("update.channel");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const u = useUpdate();
  const info = useQuery({ queryKey: ["update", "channel"], queryFn: () => request<UpdateChannelInfo>("/api/update/channel") });
  const [origin, setOrigin] = useState<string | null>(null);
  const [editing, setEditing] = useState<"custom" | null>(null);
  const save = useMutation({
    mutationFn: (body: UpdateChannelInfo) => request<UpdateChannelInfo>("/api/update/channel", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: (data) => { cache.setQueryData(["update", "channel"], data); setOrigin(null); setEditing(null); },
  });
  if (u?.message === "withSystem" || !info.data) return null;
  const current = (editing ?? info.data.channel) as (typeof CHANNELS)[number];
  const saved = info.data.origin;
  const text = origin ?? saved ?? "";
  const commit = () => { if (text.trim() && (text.trim() !== saved || info.data.channel !== current)) save.mutate({ channel: current, origin: text.trim() }); };
  const pick = (c: (typeof CHANNELS)[number]) => {
    setOrigin(null);
    // Solo Otro origen pide una ruta: Local encuentra sus paquetes en tus discos, como en Otteretro.
    if (c === "custom") return setEditing(c);
    setEditing(null);
    if (c !== info.data.channel) save.mutate({ channel: c, origin: null });
  };
  return (
    <section className="panel">
      <h2>{t("title")}</h2>
      <div className="chips" role="group" aria-label={t("title")}>
        {CHANNELS.map((c) => (
          <button key={c} type="button" className="chip chip-lg" aria-pressed={c === current} disabled={save.isPending}
            onClick={() => pick(c)}>
            {t(c)}
          </button>
        ))}
      </div>
      <p className="small muted" style={{ margin: 0 }}>{t(`${current}Hint` as "stableHint")}</p>
      {current === "local" && <p className="small" style={{ margin: 0 }}>{info.data.local ? t("localFound", { path: info.data.local }) : t("localNone")}</p>}
      {current === "custom" && (
        <form className="sentence" onSubmit={(e) => { e.preventDefault(); commit(); }}>
          <input style={{ flex: 1 }} aria-label={t("origin")}
            placeholder={t("originPlaceholder")} value={text} onChange={(e) => setOrigin(e.target.value)} />
          <button className="ot-btn" type="submit" disabled={save.isPending || !text.trim()}>{t("use")}</button>
        </form>
      )}
      {save.error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(save.error)}</p>}
    </section>
  );
}

/** Aviso visible cuando hay una versión descargada lista para instalar. */
export function UpdateNotice() {
  const t = useTranslations("update");
  const u = useUpdate();
  const restart = useMutation({ mutationFn: () => request("/api/update/restart", { method: "POST" }) });
  if (u?.phase !== "Ready") return null;
  return (
    <div className="notice" role="status">
      <img src="/nutria.png" alt="" width={32} height={32} />
      <span>{t.rich("notice", { version: u.version ?? "", b: (chunks) => <b>{chunks}</b> })}</span>
      <button className="ot-btn ot-btn-primary" type="button" disabled={restart.isPending} aria-busy={restart.isPending} onClick={() => restart.mutate()}>
        {restart.isPending && <span className="spinner" aria-hidden="true" />}
        {t("restartNow")}
      </button>
    </div>
  );
}
