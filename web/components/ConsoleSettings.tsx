"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import { optimistic } from "@/lib/optimistic";
import type { ConsoleInfo, ConsolesPage, CoversInfo, PlatformInfo } from "@/lib/types";

const GROUPS = ["nintendo", "playstation", "xbox", "pc", "other"] as const;
const groupOf = (c: ConsoleInfo): (typeof GROUPS)[number] =>
  c.id === "pc" || c.id === "pcvr" ? "pc" : c.company === "Nintendo" ? "nintendo" : c.company === "Sony" ? "playstation" : c.company === "Microsoft" ? "xbox" : "other";
const day = (iso: string) => iso.slice(0, 10);

/**
 * Ajustes › Consolas (F2.13, F2.14, #109, #192): arriba cada cuánto se pone al día el catálogo; abajo la lista de consolas
 * por empresa y, al lado, el detalle de la elegida (`?c=`). La fecha de carga va una sola vez arriba y una fila solo lleva
 * insignia si difiere.
 */
export function ConsoleSettings() {
  const t = useTranslations("settings.consoles");
  const describe = useDescribeError();
  const format = useFormatter();
  const cache = useQueryClient();
  const page = useQuery({ queryKey: ["consoles", "settings"], queryFn: () => request<ConsolesPage>("/api/consoles/settings") });
  const [adding, setAdding] = useState(false);
  const [query, setQuery] = useState("");
  const [days, setDays] = useState<number | null>(null);
  const igdb = useQuery({ queryKey: ["consoles", "igdb"], queryFn: () => request<PlatformInfo[]>("/api/consoles/igdb"), enabled: adding });
  const close = () => { setAdding(false); setQuery(""); }; // #94
  const done = (data: ConsolesPage) => {
    cache.setQueryData(["consoles", "settings"], data);
    cache.invalidateQueries({ queryKey: ["consoles", "igdb"] });
    cache.invalidateQueries({ queryKey: ["tasks"] });
  };
  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      request<ConsolesPage>(`/api/consoles/${id}`, { method: "PUT", body: JSON.stringify({ enabled }) }),
    ...optimistic(cache, ["consoles", "settings"], (d: ConsolesPage, v: { id: string; enabled: boolean }) =>
      ({ ...d, consoles: d.consoles.map((c) => (c.id === v.id ? { ...c, enabled: v.enabled } : c)) })),
    onSuccess: done,
  });
  const enable = useMutation({
    mutationFn: (igdbId: number) => request<ConsolesPage>("/api/consoles", { method: "POST", body: JSON.stringify({ igdbId }) }),
    onSuccess: done,
  });
  const saveDays = useMutation({
    mutationFn: (d: number) => request<ConsolesPage>("/api/catalog/refresh", { method: "PUT", body: JSON.stringify({ days: d }) }),
    onSuccess: (data) => { done(data); setDays(null); },
  });
  const refresh = useMutation({
    mutationFn: () => request<number>("/api/catalog/refresh", { method: "POST" }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });

  // Portadas a pedido (#132): mientras hay una descarga, el total se refresca solo.
  const covers = useQuery({ queryKey: ["covers"], queryFn: () => request<CoversInfo>("/api/covers"), refetchInterval: (q) => (q.state.data?.running ? 5_000 : false) });
  const downloadCovers = useMutation({
    mutationFn: (missing: boolean) => request<CoversInfo>(`/api/covers${missing ? "?missing=true" : ""}`, { method: "POST" }),
    onSuccess: (data) => { cache.setQueryData(["covers"], data); cache.invalidateQueries({ queryKey: ["tasks"] }); },
  });

  // #46: los TitleID y seriales de cada versión, de tablas públicas, para que Otteretro reconozca por ID.
  const fillCodes = useMutation({
    mutationFn: () => request<number>("/api/codes", { method: "POST" }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });

  const router = useRouter();
  const chosen = useSearchParams().get("c");

  if (!page.data) return page.isError ? <p className="error" role="alert">{describe(page.error)}</p> : null;
  const p = page.data;
  const date = (iso: string) => format.dateTime(new Date(iso), { day: "numeric", month: "short" });
  const enabledIds = new Set(p.consoles.filter((c) => c.enabled).map((c) => c.id));
  const text = query.trim().toLowerCase();
  const error = toggle.error ?? enable.error ?? saveDays.error ?? refresh.error ?? igdb.error;

  // La fecha de carga más común entre las habilitadas va arriba; una fila solo dice algo si difiere.
  const loaded = p.consoles.filter((c) => c.enabled && c.refreshedAt).map((c) => day(c.refreshedAt!));
  const common = loaded.sort((a, b) => loaded.filter((x) => x === b).length - loaded.filter((x) => x === a).length)[0];
  const allSame = !!common && p.consoles.every((c) => !c.enabled || (c.refreshedAt && day(c.refreshedAt) === common && c.loading == null));
  const badge = (c: ConsoleInfo) => {
    if (!c.enabled) return null;
    if (c.loading != null) return <span className="ot-badge ot-badge-info">{t("loading", { progress: Number(c.loading) })}</span>;
    if (!c.refreshedAt) return <span className="ot-badge">{Number(c.games) > 0 ? t("partial") : t("notLoaded")}</span>;
    if (day(c.refreshedAt) !== common) return <span className="ot-badge">{t("upToDateAt", { at: date(c.refreshedAt) })}</span>;
    return null;
  };

  // En la lista va solo el número de juegos; el estado de carga, en el detalle (#192).
  const status = (c?: ConsoleInfo) => c && <>{badge(c)}<span className="small muted tabular">{t("games", { count: Number(c.games) })}</span></>;

  const last = p.lastRefresh ? new Date(p.lastRefresh) : null;
  const next = last ? new Date(last.getTime() + Number(p.refreshDays) * 86_400_000) : null;
  const soon = next && next.getTime() - Date.now() < 7 * 86_400_000;
  const commitDays = () => { if (days != null && days !== Number(p.refreshDays) && days >= 1) saveDays.mutate(days); };

  return (
    <div className="settings-blocks">
      <section className="panel">
        <form className="sentence" onSubmit={(e) => { e.preventDefault(); commitDays(); }}>
          <label htmlFor="refresh-days">{t("every")}</label>
          <input id="refresh-days" type="number" min={1} max={365} style={{ width: 72 }} value={days ?? Number(p.refreshDays)}
            onChange={(e) => setDays(Number(e.target.value))} onBlur={commitDays} />
          <span>{t("days")}</span>
          {last && <span className="small muted">· {t("lastAgo", { ago: format.relativeTime(last, new Date()) })}</span>}
          {next && <span className="small muted">· {t("nextOn", { when: format.dateTime(next, soon ? { weekday: "long" } : { day: "numeric", month: "short" }) })}</span>}
          <button className="ot-btn" type="button" style={{ marginLeft: "auto" }} disabled={refresh.isPending} onClick={() => refresh.mutate()}>{t("refreshNow")}</button>
        </form>
        {refresh.isSuccess && <span className="small muted">{t("queued")}</span>}
      </section>

      {covers.data && (
        <section className="panel" aria-labelledby="covers-title">
          <div className="version-row">
            <div>
              <h2 id="covers-title">{t("covers.title")}</h2>
              <span className="small muted tabular">{t("covers.count", { have: Number(covers.data.have), total: Number(covers.data.total) })}</span>
            </div>
            {!covers.data.hasKey ? <Link className="ot-btn" href="/settings/?s=credentials">{t("covers.noKey")}</Link>
              : covers.data.running ? <button className="ot-btn" type="button" disabled>{t("covers.running")}</button>
              : <div className="sentence">
                  {/* #40: lo que falta primero; revisar todas trae también las que cambiaron. */}
                  <button className="ot-btn ot-btn-primary" type="button" disabled={downloadCovers.isPending} onClick={() => downloadCovers.mutate(true)}>{t("covers.missing")}</button>
                  <button className="ot-btn" type="button" disabled={downloadCovers.isPending} onClick={() => downloadCovers.mutate(false)}>{t("covers.download")}</button>
                </div>}
          </div>
          {downloadCovers.error && <p className="error small" role="alert">{describe(downloadCovers.error)}</p>}
          <div className="version-row">
            <span className="small muted">{t("codes.help")}</span>
            <button className="ot-btn" type="button" disabled={fillCodes.isPending} onClick={() => fillCodes.mutate()}>{t("codes.fill")}</button>
          </div>
        </section>
      )}

      <section className="panel">
        <div className="version-row">
          <span className="small muted">{common ? (allSame ? t("allUpToDate", { at: date(common) }) : t("mostUpToDate", { at: date(common) })) : t("help")}</span>
          {!adding && <button className="ot-btn" type="button" onClick={() => setAdding(true)}>{t("add")}</button>}
        </div>
        {adding && (
          <div style={{ display: "grid", gap: 6 }}>
            <div style={{ display: "flex", gap: 6 }}>
              <input type="search" style={{ flex: 1 }} aria-label={t("search")} placeholder={t("search")} value={query} onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Escape") close(); }} autoFocus />
              <button className="ot-btn" type="button" onClick={close}>{t("close")}</button>
            </div>
            <ul className="console-list" style={{ maxHeight: 260, overflowY: "auto" }}>
              {igdb.data?.filter((x) => !text || x.name.toLowerCase().includes(text) || x.abbreviation?.toLowerCase().includes(text)).map((x) => {
                const added = x.consoleId != null && enabledIds.has(x.consoleId);
                return (
                  <li key={x.igdbId} className="console-row">
                    <span className="console-name">{x.name}{x.abbreviation ? <span className="muted"> · {x.abbreviation}</span> : null}</span>
                    {added ? <span className="small muted">{t("already")}</span>
                      : <button className="ot-btn ot-btn-sm" type="button" disabled={enable.isPending} onClick={() => enable.mutate(Number(x.igdbId))}>{t("enable")}</button>}
                  </li>
                );
              })}
            </ul>
          </div>
        )}
        <div className="console-split">
          <div className="console-list">
            {GROUPS.map((g) => {
              const rows = p.consoles.filter((c) => groupOf(c) === g);
              if (rows.length === 0) return null;
              return (
                <div key={g}>
                  <h3 className="small muted console-group">{t(`group.${g}`)}</h3>
                  <ul className="console-list">
                    {rows.map((c) => (
                      <li key={c.id} className={`console-row${c.enabled ? "" : " muted"}`} aria-current={c.id === chosen}>
                        <input type="checkbox" role="switch" aria-label={c.name} checked={c.enabled} onChange={(e) => toggle.mutate({ id: c.id, enabled: e.target.checked })} />
                        <button type="button" className="link-button console-name" aria-label={t("detail.open", { name: c.name })}
                          onClick={() => router.push(`/settings/?s=consoles&c=${c.id}`)}>
                          <b>{c.name}</b>
                        </button>
                        <span className="small muted tabular console-games">{Number(c.games)}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              );
            })}
          </div>
        </div>
      </section>
      {error && <p className="error small" role="alert">{describe(error)}</p>}
    </div>
  );
}

const detailKey = (id: string) => ["consoles", "detail", id];
