"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { ImagesFolderInfo, ImagesFolderRequest } from "@/lib/types";

/** Ajustes › General › Imágenes (#9): la carpeta propia, la de Otteretro en uno de sus discos, u otra. */
export function ImagesFolder() {
  const t = useTranslations("settings.images");
  const describe = useDescribeError();
  const cache = useQueryClient();
  const [other, setOther] = useState("");
  const { data } = useQuery({ queryKey: ["images-folder"], queryFn: () => request<ImagesFolderInfo>("/api/images-folder") });
  const change = useMutation({
    mutationFn: (body: ImagesFolderRequest) => request<ImagesFolderInfo>("/api/images-folder", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: (info) => {
      cache.setQueryData(["images-folder"], info);
      setOther("");
    },
  });
  if (!data) return null;
  return (
    <div className="form-block">
      <span className="small muted">{t("text")}</span>
      <div className="chips" role="group" aria-label={t("title")}>
        <button type="button" className="chip chip-lg" aria-pressed={!data.shared} disabled={change.isPending}
          onClick={() => data.shared && change.mutate({ path: null })}>{t("own")}</button>
        {data.disks.map((d) => (
          <button key={d.name} type="button" className="chip chip-lg" aria-pressed={data.shared && d.folder === data.path}
            disabled={change.isPending || !d.hasImages} title={d.hasImages ? t("hasImages") : t("noImages")}
            onClick={() => change.mutate({ path: d.folder })}>{t("disk", { disk: d.name })}</button>
        ))}
      </div>
      {data.disks.map((d) => (
        <span key={d.name} className="small muted">{t("disk", { disk: d.name })}: {d.hasImages ? t("hasImages") : t("noImages")}</span>
      ))}
      {data.disks.length === 0 && <span className="small muted">{t("noDisks")}</span>}
      <form className="version-row" onSubmit={(e) => { e.preventDefault(); if (other.trim()) change.mutate({ path: other.trim() }); }}>
        <input style={{ flex: 1 }} type="text" aria-label={t("other")} placeholder={t("other")} value={other} onChange={(e) => setOther(e.target.value)} />
        <button className="ot-btn ot-btn-sm" type="submit" disabled={change.isPending || !other.trim()}>{t("use")}</button>
      </form>
      <span className="small muted">{t("current", { path: data.path })}</span>
      {!data.available && data.shared && <span className="small" role="alert">{t("missing", { path: data.path })}</span>}
      {change.error && <span className="small" role="alert">{describe(change.error)}</span>}
    </div>
  );
}
