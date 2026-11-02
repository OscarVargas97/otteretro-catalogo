"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useLocale, useTranslations } from "next-intl";
import { request } from "@/lib/client";
import type { AppState, LanguageRequest } from "@/lib/types";
import { CATALOGS, LOCALES, type Locale } from "@/messages";

/** Elegir el idioma: se guarda en config.json y la interfaz cambia sin reiniciar (#140). */
export function LanguagePicker() {
  const t = useTranslations("settings");
  const locale = useLocale();
  const cache = useQueryClient();
  const change = useMutation({
    mutationFn: (body: LanguageRequest) => request<AppState>("/api/language", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: (state) => cache.setQueryData(["state"], state),
  });
  return (
    // Dos botones (#111). Cada idioma se muestra con su propio nombre (languageName de su catálogo).
    <div className="chips" role="group" aria-label={t("language")}>
      {LOCALES.map((l) => (
        <button key={l} type="button" className="chip chip-lg" lang={l} aria-pressed={l === locale} disabled={change.isPending}
          onClick={() => l !== locale && change.mutate({ language: l as Locale })}>
          {CATALOGS[l].languageName}
        </button>
      ))}
    </div>
  );
}
