"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Suspense, useDeferredValue, useEffect, useRef, useState } from "react";
import { eventsUrl, request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { CatalogCatchUp, SearchResults } from "@/lib/types";
import { gameQuery, sagaQuery } from "@/lib/queries";
import { useIdParam } from "./Catalog";
import { UpdateTask, useUpdate } from "./Update";
import { MoveTask, useMove } from "./DataFolder";
import { ClearTasks, TaskRows, upsertTask, useTasks } from "./Tasks";
import { Empty, useAppState } from "./Shell";
import { PublishButton } from "./Publish";

/** Título de la pantalla (en Saga y Juego, el camino), búsqueda global (Ctrl+K) y panel de tareas en segundo plano. Fija al bajar (#169). */
export function TopBar({ title, crumbs }: { title: string; crumbs?: "/saga" | "/game" }) {
  const t = useTranslations("topBar");
  const [searching, setSearching] = useState(false);
  const [tasksOpen, setTasksOpen] = useState(false);
  const connected = useEvents();
  const fullscreen = !!useAppState().data?.fullscreen;
  const describe = useDescribeError();
  const update = useUpdate();
  const move = useMove();
  const tasks = useTasks();
  const moving = !!move?.running;
  const hasTasks = tasks.length > 0 || (!!update && ["Checking", "Downloading", "Ready", "Error"].includes(update.phase)) || (!!move && (move.running || ["done", "error"].includes(move.step)));
  const active = Number(update?.phase === "Checking" || update?.phase === "Downloading") + Number(moving)
    + tasks.filter((t) => t.status === "queued" || t.status === "running").length;
  const busy = active > 0;
  // Su alto, para que lo fijo de cada pantalla (filtros de Sagas) quede justo debajo (#170).
  const header = useRef<HTMLElement>(null);
  useEffect(() => {
    const el = header.current;
    if (!el) return;
    const observer = new ResizeObserver(() => document.documentElement.style.setProperty("--top-h", `${el.offsetHeight}px`));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setSearching(true);
      }
      if (e.key === "Escape") {
        setSearching(false);
        setTasksOpen(false);
      }
    };
    const openTasks = () => setTasksOpen(true);
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  const catchUp = useCatchUp();
  return (
    <header className="top" ref={header}>
      <h1 className={crumbs ? "sr-only" : undefined}>{title}</h1>
      {crumbs && <Suspense fallback={null}><Crumbs path={crumbs} /></Suspense>}
      <button className="search" type="button" onClick={() => setSearching(true)}>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
          <circle cx="11" cy="11" r="7" />
          <path d="M20 20l-4-4" />
        </svg>
        <span className="search-text">{t("search")}</span>
        <span className="kbd">{t("shortcut")}</span>
      </button>
      {catchUp?.status === "conflicts" && (
        <Link className="ot-btn" href="/settings/?s=catalog" role="status">
          {t("catchUpConflicts", { count: Number(catchUp.conflicts) })}{" · "}{t("catchUpResolve")}
        </Link>
      )}
      <PublishButton kind="all" id={0} />
      <button className="ot-btn" type="button" aria-expanded={tasksOpen} onClick={() => setTasksOpen(!tasksOpen)}>
        <span className="dot" data-connected={connected} data-busy={busy} />
        {t("tasks")}
        <span className="ot-count">{active}</span>
      </button>

      {tasksOpen && (
        <div className="ot-tasks tasks-panel" role="dialog" aria-label={t("tasksPanel")}>
          <UpdateTask />
          <MoveTask />
          <TaskRows />
          <ClearTasks />
          {!hasTasks && <Empty title={t("noTasksTitle")} text={t("noTasksText")} />}
          <p className="small muted tasks-panel-footer">{connected ? t("connected") : t("reconnecting")}</p>
        </div>
      )}

      {searching && <Search onClose={() => setSearching(false)} />}
    </header>
  );
}

/**
 * Ponerse al día solo (#106, decisiones 372 y 373): al abrir y cada 5 minutos el servidor fusiona el catálogo local con el último
 * commit de main si avanzó desde la base; si quedan conflictos, la barra avisa. Publicar la vuelve a pedir (Publish.tsx).
 */
function useCatchUp() {
  const cache = useQueryClient();
  const { data } = useQuery({
    queryKey: ["catalog", "catchup"],
    queryFn: () => request<CatalogCatchUp>("/api/catalog/catchup", { method: "POST" }),
    refetchInterval: 5 * 60_000,
    retry: false,
  });
  const applied = data?.status === "applied" ? data.tag : null;
  // Lo aplicado cambia sagas, juegos e imágenes: se vuelve a pedir todo salvo la propia sonda.
  useEffect(() => {
    if (applied) cache.invalidateQueries({ predicate: (q) => q.queryKey[1] !== "catchup" });
  }, [applied, cache]);
  return data;
}

/** «Sagas › saga › juego» (#169). Sale de las mismas consultas que la pantalla: no pide nada aparte. */
function Crumbs({ path }: { path: "/saga" | "/game" }) {
  const t = useTranslations();
  const id = useIdParam() ?? "";
  const saga = useQuery({ ...sagaQuery(id), enabled: path === "/saga" && id !== "" });
  const game = useQuery({ ...gameQuery(id), enabled: path === "/game" && id !== "" });
  const sagaName = (sagaId: number | string | null | undefined, name: string | null | undefined) =>
    Number(sagaId ?? 0) === 0 ? t("catalog.emulation") : name ?? "";
  const items: { href?: string; label: string }[] = [{ href: "/sagas/", label: t("nav.sagas") }];
  if (path === "/saga" && saga.data) items.push({ label: sagaName(saga.data.saga.id, saga.data.saga.name) });
  if (path === "/game" && game.data) {
    const g = game.data;
    items.push({ href: `/saga/?id=${g.sagaId ?? 0}`, label: sagaName(g.sagaId, g.sagaName) }, { label: g.nameEs });
  }
  return (
    <nav className="crumbs" aria-label={t("topBar.crumbs")}>
      <ol>
        {items.map((c, i) => (
          <li key={i}>{c.href ? <Link href={c.href}>{c.label}</Link> : <span aria-current="page">{c.label}</span>}</li>
        ))}
      </ol>
    </nav>
  );
}

/** Búsqueda global (§0.6): sagas y juegos por nombre, en español u original. Enter abre el primero. */
function Search({ onClose }: { onClose: () => void }) {
  const t = useTranslations("topBar");
  const tc = useTranslations("catalog");
  const router = useRouter();
  const field = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const text = useDeferredValue(query.trim());
  const results = useQuery({
    queryKey: ["search", text],
    queryFn: () => request<SearchResults>(`/api/search?q=${encodeURIComponent(text)}`),
    enabled: text.length >= 2,
    placeholderData: (previous) => previous,
  });
  useEffect(() => field.current?.focus(), []);
  const links = [
    ...(results.data?.sagas ?? []).map((s) => ({ href: `/saga/?id=${s.id}`, name: s.name, meta: tc("games", { count: Number(s.games) }), group: "sagas" as const })),
    ...(results.data?.games ?? []).map((g) => ({ href: `/game/?id=${g.id}`, name: g.name, meta: [g.sagaName, g.year].filter(Boolean).join(" · "), group: "games" as const })),
  ];
  const open = (href: string) => {
    onClose();
    router.push(href);
  };

  return (
    <div className="overlay" onClick={onClose}>
      <div className="search-dialog" role="dialog" aria-label={t("searchLabel")} onClick={(e) => e.stopPropagation()}>
        <label htmlFor="search" className="small muted">{t("searchLabel")}</label>
        <input id="search" ref={field} placeholder={t("searchPlaceholder")} value={query} autoComplete="off"
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter" && links[0]) open(links[0].href); }} />
        {text.length < 2 ? (
          <p className="small muted">{tc("search.typeMore")}</p>
        ) : results.data && links.length === 0 ? (
          <p className="small muted">{tc("search.noResults", { query: text })}</p>
        ) : (
          (["sagas", "games"] as const).map((group) => links.some((l) => l.group === group) && (
            <div key={group} className="search-group">
              <h3 className="small muted">{tc(`search.${group}`)}</h3>
              {links.filter((l) => l.group === group).map((l) => (
                <Link key={l.href} className="search-result" href={l.href} onClick={(e) => { e.preventDefault(); open(l.href); }}>
                  <span>{l.name}</span>
                  <span className="small muted">{l.meta}</span>
                </Link>
              ))}
            </div>
          ))
        )}
      </div>
    </div>
  );
}

/** Conexión de Server-Sent Events con la app; el navegador reconecta solo. */
function useEvents() {
  const [connected, setConnected] = useState(false);
  const cache = useQueryClient();
  useEffect(() => {
    const source = new EventSource(eventsUrl());
    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);
    source.addEventListener("update", (e) => cache.setQueryData(["update"], JSON.parse(e.data)));
    source.addEventListener("move", (e) => cache.setQueryData(["move"], JSON.parse(e.data)));
    source.addEventListener("task", (e) => upsertTask(cache, JSON.parse(e.data)));
    // Al reconectar (por ejemplo, tras reiniciar la app) se vuelve a pedir la lista.
    source.addEventListener("open", () => cache.invalidateQueries({ queryKey: ["tasks"] }));
    return () => source.close();
  }, [cache]);
  return connected;
}
