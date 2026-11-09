"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect } from "react";
import { request } from "@/lib/client";
import type { AppState } from "@/lib/types";
import { normalizePath, useAppState } from "./Shell";

// Clave en nav.* de cada entrada; `phase`, la fase en que llega: hasta entonces se ve atenuada y no se abre (#169).
type Entry = { href: string; key: "home" | "sagas" | "review" | "emulators" | "settings"; phase?: number };
const MAIN: Entry[] = [
  { href: "/", key: "home" },
  { href: "/sagas/", key: "sagas" },
  { href: "/review/", key: "review" },
  { href: "/emulators/", key: "emulators" },
];
const SECONDARY: Entry[] = [
  { href: "/settings/", key: "settings" },
];

// Un ícono por entrada (#130), para la franja del menú retraído. Trazos de 24×24.
const ICONS: Record<Entry["key"], string> = {
  home: "M3 11 12 4l9 7M5 10v10h5v-6h4v6h5V10",
  sagas: "M4 5h6v14H4zM14 5h6v6h-6zM14 15h6v4h-6z",
  review: "M5 12l4 4L19 6",
  emulators: "M6 8h12a4 4 0 0 1 0 8H6a4 4 0 0 1 0-8zM8 11v2M7 12h2M15 11h.01M17 13h.01",
  settings: "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2",
};
const Icon = ({ d }: { d: string }) => (
  <svg className="nav-icon" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={d} /></svg>
);

/** Barra lateral (componente NavItem del sistema de diseño). Los contadores llegan con cada módulo; Revisar lleva el suyo (D3). */
export function Sidebar() {
  const t = useTranslations("nav");
  // Sin carpeta de datos la API responde 409: sin contador.
  // El menú se dibuja antes de saber si hay carpeta de datos (#105); sin ella no hay qué contar.
  const configured = !!useAppState().data?.configured;
  const review = useQuery({ queryKey: ["review", "count"], queryFn: () => request<number>("/api/review/count"), refetchInterval: 60_000, retry: false, enabled: configured });
  const counts: Partial<Record<string, number>> = { review: Number(review.data ?? 0) };
  const current = normalizePath(usePathname());
  // Retraída a una franja de íconos (#130): se guarda en config.json y Ctrl+B la alterna.
  const cache = useQueryClient();
  const collapsed = !!useAppState().data?.menuCollapsed;
  const save = useMutation({
    mutationFn: (value: boolean) => request<AppState>("/api/menu", { method: "PUT", body: JSON.stringify({ collapsed: value }) }),
    onMutate: (value) => cache.setQueryData<AppState>(["state"], (s) => s && { ...s, menuCollapsed: value }),
    onSuccess: (state) => cache.setQueryData(["state"], state),
  });
  const toggle = () => save.mutate(!collapsed);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "b") { e.preventDefault(); toggle(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });
  const isActive = (href: string) => {
    const target = normalizePath(href);
    return target === current || (target !== "/" && current.startsWith(target)) ? "page" : undefined;
  };
  const item = (e: Entry) => e.phase ? (
    <span key={e.href} className="ot-nav-item is-future" aria-disabled="true" title={collapsed ? `${t(e.key)} · ${t("comingIn", { phase: e.phase })}` : t("comingIn", { phase: e.phase })}>
      <Icon d={ICONS[e.key]} />
      <span className="nav-label">{t(e.key)}</span>
      <span className="ot-badge">F{e.phase}</span>
    </span>
  ) : (
    <Link key={e.href} className="ot-nav-item" href={e.href} aria-current={isActive(e.href)} title={collapsed ? t(e.key) : undefined}>
      <Icon d={ICONS[e.key]} />
      <span className="nav-label">{t(e.key)}</span>
      {!!counts[e.key] && <span className="ot-count" aria-label={t("pending", { count: counts[e.key]! })}>{counts[e.key]}</span>}
    </Link>
  );

  return (
    <aside className={`sidebar${collapsed ? " is-collapsed" : ""}`}>
      <Link href="/" className="sidebar-logo">
        <img src="/nutria.png" alt="" width={32} height={32} />
        <span className="nav-label">otteretro</span>
      </Link>
      <nav className="ot-nav sidebar-nav" aria-label={t("label")}>
        {MAIN.map(item)}
        <div className="ot-nav-sep" />
        {SECONDARY.map(item)}
      </nav>
      <button type="button" className="ot-nav-item sidebar-toggle" onClick={toggle} aria-expanded={!collapsed} aria-keyshortcuts="Control+B"
        title={collapsed ? t("expand") : undefined}>
        <Icon d={collapsed ? "M9 6l6 6-6 6" : "M15 6l-6 6 6 6"} />
        <span className="nav-label">{t(collapsed ? "expand" : "collapse")}</span>
      </button>
    </aside>
  );
}
