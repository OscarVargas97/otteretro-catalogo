"use client";

import { useLocale, useTranslations } from "next-intl";
import { ApiRequestError } from "./client";
import { gb } from "./format";
import type { ApiError } from "./types";

/** Parámetros que el backend manda en bytes y se muestran como "812 GB". */
const BYTE_PARAMS = new Set(["needed", "free"]);
/** Parámetros que llegan como fecha ISO y se muestran como "vie, 03:10" en la hora del equipo. */
const TIME_PARAMS = new Set(["at"]);
const time = (iso: string, locale: string) =>
  new Date(iso).toLocaleString(locale, { weekday: "short", hour: "2-digit", minute: "2-digit" });

/**
 * Traductor para claves que llegan de la API (códigos de error, pasos de la mudanza, motivos del actualizador).
 * Esas claves no se conocen al compilar, así que se pierde el chequeo de tipos solo en este punto.
 */
export function useDynamicT() {
  const t = useTranslations();
  return t as unknown as { (key: string, values?: Record<string, string | number>): string; has(key: string): boolean };
}

/** Convierte un error de la API (o cualquier otro) en el texto del idioma activo (#142). */
export function useDescribeError() {
  const t = useDynamicT();
  const locale = useLocale();
  return (e: unknown): string => {
    const error: ApiError | null = e instanceof ApiRequestError ? e.error : isApiError(e) ? e : null;
    if (!error) return e instanceof Error ? e.message : String(e);
    const params = Object.fromEntries(
      Object.entries(error.params ?? {}).map(([k, v]) => [k, BYTE_PARAMS.has(k) ? gb(v, locale) : TIME_PARAMS.has(k) ? time(v, locale) : v]),
    );
    const key = `errors.${error.code}`;
    return t.has(key) ? t(key, params) : t("errors.unknown", { code: error.code });
  };
}

const isApiError = (e: unknown): e is ApiError => typeof e === "object" && e !== null && "code" in e;
