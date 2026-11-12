"use client";

import { QueryClient, QueryClientProvider, useQuery, useQueryClient } from "@tanstack/react-query";
import { NextIntlClientProvider, useTranslations } from "next-intl";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { Suspense, useEffect, useLayoutEffect, useSyncExternalStore, type ReactNode } from "react";
import { request } from "@/lib/client";
import { prefetchLink } from "@/lib/queries";
import { useDescribeError } from "@/lib/i18n";
import type { AppState } from "@/lib/types";
import { CATALOGS, isLocale, systemLocale } from "@/messages";
import { Sidebar } from "./Sidebar";
import { UpdateNotice } from "./Update";
import { TopBar } from "./TopBar";

// Clave del título (nav.*) de cada pantalla.
const TITLES = {
  "/": "home",
  "/sagas": "sagas",
  "/saga": "saga",
  "/game": "game",
  "/review": "review",
  "/review/history": "history",
  "/emulators": "emulators",
  "/settings": "settings",
} as const;

/** Quita la barra final que añade trailingSlash, salvo en la raíz. */
export const normalizePath = (path: string) => (path.length > 1 ? path.replace(/\/$/, "") : path);

// Lo ya visto se muestra al instante (#105): fresco por 10 s y guardado media hora; pasado eso se refresca por detrás.
const queryClient = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false, staleTime: 10_000, gcTime: 30 * 60_000 } } });

export const useAppState = () => useQuery({ queryKey: ["state"], queryFn: () => request<AppState>("/api/state") });

export function Shell({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <Translations>
        <Content>{children}</Content>
        <Prefetch />
        <Suspense fallback={null}><ScrollTop /></Suspense>
      </Translations>
    </QueryClientProvider>
  );
}

/** Carga el catálogo del idioma elegido (#140). Hasta saberlo, el del sistema. */
function Translations({ children }: { children: ReactNode }) {
  const language = useAppState().data?.language;
  const locale = isLocale(language) ? language : systemLocale();
  useEffect(() => {
    document.documentElement.lang = locale;
  }, [locale]);
  return (
    <NextIntlClientProvider locale={locale} messages={CATALOGS[locale]} timeZone={Intl.DateTimeFormat().resolvedOptions().timeZone}>
      {children}
    </NextIntlClientProvider>
  );
}

function Content({ children }: { children: ReactNode }) {
  const t = useTranslations();
  const describe = useDescribeError();
  const path = normalizePath(usePathname());
  const router = useRouter();
  const state = useAppState();
  const inWizard = path === "/welcome";
  // En el navegador ya, sin esperar a la API: la página estática no sabe el idioma y no puede llevar el marco (#105).
  const mounted = useSyncExternalStore(() => () => {}, () => true, () => false);

  // Sin carpeta de datos, todo lleva al asistente de primer arranque.
  useEffect(() => {
    if (state.data && !state.data.configured && !inWizard) router.replace("/welcome/");
  }, [state.data, inWizard, router]);

  if (state.isError) return <p className="content">{t("app.unreachable", { detail: describe(state.error) })}</p>;
  if (inWizard) return state.data ? <>{children}</> : null;
  if (!mounted) return null;

  const title = TITLES[path as keyof typeof TITLES];
  return (
    <div className="app">
      <Sidebar />
      <main className="app-main">
        {/* En Saga y Juego el título es el del héroe; la barra lleva el camino (#169). */}
        <TopBar title={title ? t(`nav.${title}`) : "Otteretro Catálogo"} crumbs={path === "/saga" || path === "/game" ? path : undefined}
          action={path === "/review" ? <Link className="ot-btn ot-btn-ghost" href="/review/history/">{t("nav.history")}</Link> : undefined} />
        <UpdateNotice />
        {/* El marco va primero (#105); la pantalla, cuando se sabe que hay carpeta de datos (sin ella sus consultas fallan). */}
        {state.data?.configured && children}
      </main>
    </div>
  );
}

// «Atrás» y «Adelante» llegan por popstate: ahí el navegador devuelve la altura que había.
let fromHistory = false;
if (typeof window !== "undefined") window.addEventListener("popstate", () => { fromHistory = true; });

/** Otra pantalla (o la misma con otro `?id=`) empieza arriba (#128); «Atrás» vuelve a donde estabas. */
function ScrollTop() {
  const where = usePathname() + "?" + useSearchParams();
  useLayoutEffect(() => {
    if (fromHistory) fromHistory = false;
    else window.scrollTo(0, 0);
  }, [where]);
  return null;
}

/** Al pasar el ratón o el foco (también el del mando) por un enlace, pide sus datos antes del clic (#105). */
function Prefetch() {
  const client = useQueryClient();
  useEffect(() => {
    const onHover = (e: Event) => {
      const link = (e.target as Element | null)?.closest?.("a[href]");
      if (link) prefetchLink(client, link.getAttribute("href")!);
    };
    document.addEventListener("pointerover", onHover);
    document.addEventListener("focusin", onHover);
    return () => {
      document.removeEventListener("pointerover", onHover);
      document.removeEventListener("focusin", onHover);
    };
  }, [client]);
  return null;
}

/** Estado vacío con la nutria y, si la hay, la acción que lo resuelve. */
export function Empty({ title, text, children }: { title: string; text: string; children?: ReactNode }) {
  return (
    <div className="ot-empty">
      <img src="/nutria.png" alt="" width={64} height={64} />
      <h3>{title}</h3>
      <p>{text}</p>
      {children}
    </div>
  );
}

/** Para pantallas que se activan en una fase posterior. `screen` es su clave en comingSoon.*. */
export function ComingSoon({ screen, phase }: { screen: "home" | "discover" | "review" | "downloads" | "tools" | "steam"; phase: number }) {
  const t = useTranslations("comingSoon");
  return (
    <div className="content">
      <Empty title={t(`${screen}.title`)} text={t(`${screen}.text`)}>
        <span className="ot-badge">{t("badge", { phase })}</span>
      </Empty>
    </div>
  );
}
