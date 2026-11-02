"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { ImagesFolderInfo, StarRequest } from "@/lib/types";

/**
 * Imágenes (#9): van en el disco ★ de Otteretro y lo siguen. Con Otteretro instalado solo se muestra dónde están (el ★ se cambia
 * desde Otteretro); sin él, se elige el disco y se prepara como lo haría Otteretro.
 */
export function ImagesFolder() {
  const t = useTranslations("settings.images");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const { data } = useQuery({ queryKey: ["images-folder"], queryFn: () => request<ImagesFolderInfo>("/api/images-folder") });
  const change = useMutation({
    mutationFn: (body: StarRequest) => request<ImagesFolderInfo>("/api/images-folder", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: (info) => cache.setQueryData(["images-folder"], info),
  });
  if (!data) return null;
  const { home, disks } = data;
  return (
    <div className="form-block">
      {home.folder && <span>{t("current", { path: home.folder })}</span>}
      {home.managedByOtteretro
        ? <span className="small muted">{t("managed")}</span>
        : (
          <>
            <span className="small muted">{home.star ? t("chooseOther") : t("choose")}</span>
            <div className="chips" role="group" aria-label={t("title")}>
              {disks.map((d) => (
                <button key={d.name} type="button" className="chip chip-lg" aria-pressed={d.folder === home.star} disabled={change.isPending}
                  onClick={() => d.folder !== home.star && change.mutate({ star: d.folder })}>
                  ★ {d.name}{d.hasOtteretro ? ` · ${t("hasOtteretro")}` : ""}
                </button>
              ))}
            </div>
            {home.star && <span className="small muted">{t("moves")}</span>}
          </>
        )}
      {home.star && !home.available && <span className="small" role="alert">{t("missing", { path: home.star })}</span>}
      {change.isPending && <span className="small muted" role="status">{t("preparing")}</span>}
      {change.error && <span className="small" role="alert">{describe(change.error)}</span>}
    </div>
  );
}
