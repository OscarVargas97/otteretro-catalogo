"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { ApiRequestError, request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { PublishPreview, PublishRequest, PublishResult } from "@/lib/types";

/**
 * «Publicar» (#22): muestra qué cambiaría el PR comparado con main y lo abre (o actualiza el que ya está abierto). Publicar solo
 * propone el cambio: el PR lo fusiona quien administra el repo.
 */
export function PublishButton({ kind, id }: PublishRequest) {
  const t = useTranslations("publish");
  const [open, setOpen] = useState(false);
  // «Publicar todo» va en la barra de arriba con cuántos archivos llevaría (#85); el servidor reusa la comparación mientras nada cambie.
  const count = useQuery({
    queryKey: ["publish", "count"],
    queryFn: () => request<number | null>("/api/publish/count"),
    enabled: kind === "all",
    staleTime: 60_000,
    retry: false,
  });
  return (
    <>
      <button className="ot-btn" type="button" aria-expanded={open} onClick={() => setOpen(!open)}>
        {kind === "all" ? t("buttonAll") : t("button")}
        {kind === "all" && count.data != null && <span className="ot-count">{count.data}</span>}
      </button>
      {open && <PublishPanel kind={kind} id={id} onClose={() => setOpen(false)} />}
    </>
  );
}

const SHOWN = 200;

function PublishPanel({ kind, id, onClose }: PublishRequest & { onClose: () => void }) {
  const t = useTranslations("publish");
  const describe = useDescribeError();
  const body = JSON.stringify({ kind, id } satisfies PublishRequest);
  const preview = useQuery({
    queryKey: ["publish", kind, String(id)],
    queryFn: () => request<PublishPreview>("/api/publish/preview", { method: "POST", body }),
    // Al volver se muestra la última al instante y se confirma con el servidor, que la reusa si nada cambió (#85).
    staleTime: 0,
    retry: false,
  });
  const publish = useMutation({ mutationFn: () => request<PublishResult>("/api/publish", { method: "POST", body }) });
  const p = preview.data;
  const noKey = preview.error instanceof ApiRequestError && preview.error.error.code === "publish.noKey";
  const blocked = !p || p.files.length === 0 || p.errors.length > 0 || p.conflicts.length > 0;

  return (
    <div className="overlay" onClick={onClose}>
      <div className="search-dialog publish-dialog" role="dialog" aria-label={t("title")} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ margin: 0 }}>{p ? t("titleOf", { name: p.title }) : t("title")}</h3>
        {preview.isPending && <p className="small muted" role="status"><span className="spinner" aria-hidden="true" /> {t("comparing")}</p>}
        {noKey ? (
          <div className="panel">
            <p className="small" style={{ margin: 0 }}>{t("noKey")}</p>
            <Link className="ot-btn ot-btn-primary" href="/settings/?s=credentials">{t("openCredentials")}</Link>
          </div>
        ) : preview.error && <p className="error small" role="alert">{describe(preview.error)}</p>}

        {p && (
          <>
            {p.files.length === 0 && <p className="small muted">{t("nothing")}</p>}
            <ul className="publish-files">
              {/* «Publicar todo» puede tocar miles de archivos: se listan los primeros y el total. */}
              {p.files.slice(0, SHOWN).map((f) => (
                <li key={f.path}>
                  <span className={`ot-badge${f.change === "deleted" ? " ot-badge-danger" : f.change === "added" ? " ot-badge-have" : ""}`}>{t(`change.${f.change as "added"}`)}</span>{" "}
                  <code className="small">catalogo/{f.path}</code>
                  {f.details.length > 0 && <ul className="small muted">{f.details.map((d, i) => <li key={i}>{describe(d)}</li>)}</ul>}
                </li>
              ))}
              {p.files.length > SHOWN && <li className="small muted">{t("more", { count: p.files.length - SHOWN })}</li>}
            </ul>
            {p.errors.length > 0 && (
              <div className="result" role="alert">
                <span className="ot-badge ot-badge-danger">{t("invalid")}</span>
                <ul className="small" style={{ margin: 0 }}>{p.errors.slice(0, 10).map((e) => <li key={e}>{e}</li>)}</ul>
              </div>
            )}
            {p.conflicts.length > 0 && (
              <div className="result" role="alert">
                <span className="ot-badge ot-badge-danger">{t("conflictTitle")}</span>
                <span className="small">{t("conflict", { files: p.conflicts.join(", ") })}</span>
              </div>
            )}
            {!p.knownBase && p.files.length > 0 && <p className="small muted">{t("unknownBase")}</p>}
            {p.openPr && <p className="small">{t("openPr")} <a href={p.openPr} target="_blank" rel="noreferrer">{p.openPr}</a></p>}
          </>
        )}

        {publish.data ? (
          <p className="small" role="status">{t(publish.data.updated ? "updated" : "opened")} <a href={publish.data.url} target="_blank" rel="noreferrer">{publish.data.url}</a></p>
        ) : (
          <div className="sentence" style={{ justifyContent: "flex-end" }}>
            <span className="small muted" style={{ flex: 1 }}>{t("noMerge")}</span>
            <button className="ot-btn ot-btn-ghost" type="button" onClick={onClose}>{t("close")}</button>
            <button className="ot-btn ot-btn-primary" type="button" disabled={blocked || publish.isPending} aria-busy={publish.isPending} onClick={() => publish.mutate()}>
              {publish.isPending && <span className="spinner" aria-hidden="true" />}
              {t(p?.openPr ? "update" : "open")}
            </button>
          </div>
        )}
        {publish.error && <p className="error small" role="alert">{describe(publish.error)}</p>}
      </div>
    </div>
  );
}
