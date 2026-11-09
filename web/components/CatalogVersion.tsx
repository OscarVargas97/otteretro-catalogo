"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { request } from "@/lib/client";
import { useDescribeError, useDynamicT } from "@/lib/i18n";
import type { CatalogApplyResult, CatalogConflict, CatalogVersionsPage } from "@/lib/types";

/**
 * Ajustes › Catálogo (#105, decisión 363): el catálogo local (base, totales y cuántos cambios sin publicar), las versiones publicadas del
 * repo al que se publica con lo que trajo cada una, y los conflictos que dejó la última fusión para elegir uno por uno (359).
 */
export function CatalogVersion() {
  const t = useTranslations("settings.catalog");
  const dt = useDynamicT();
  const describe = useDescribeError();
  const format = useFormatter();
  const cache = useQueryClient();
  const page = useQuery({ queryKey: ["catalog", "versions"], queryFn: () => request<CatalogVersionsPage>("/api/catalog/versions") });
  // La misma consulta que el botón de Publicar de la barra de arriba (#85).
  const unpublished = useQuery({ queryKey: ["publish", "count"], queryFn: () => request<number | null>("/api/publish/count") });
  const conflicts = useQuery({ queryKey: ["catalog", "conflicts"], queryFn: () => request<CatalogConflict[]>("/api/catalog/conflicts") });
  const apply = useMutation({
    mutationFn: (tag: string) => request<CatalogApplyResult>("/api/catalog/version/apply", { method: "POST", body: JSON.stringify({ tag }) }),
    // La fusión cambia sagas, juegos, imágenes y lo que hay por publicar: todo se vuelve a pedir.
    onSuccess: () => cache.invalidateQueries(),
  });
  const resolve = useMutation({
    mutationFn: ({ id, useRemote }: { id: number | string; useRemote: boolean }) =>
      request<CatalogConflict[]>(`/api/catalog/conflicts/${id}`, { method: "POST", body: JSON.stringify({ useRemote }) }),
    onSuccess: (left) => {
      cache.setQueryData(["catalog", "conflicts"], left);
      cache.invalidateQueries({ predicate: (q) => q.queryKey[0] !== "catalog" });
    },
  });

  const pending = conflicts.data?.length ?? 0;
  const day = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { day: "numeric", month: "short", year: "numeric" }) : "");
  const error = page.error ?? apply.error ?? resolve.error;
  const data = page.data;

  return (
    <div className="settings-blocks">
      <section className="panel" aria-labelledby="catalog-current">
        <h2 id="catalog-current">{t("inUse")}</h2>
        {data && (
          <>
            <p className="small tabular" style={{ margin: 0 }}>
              {t("base")} <b>{data.base ? data.base.slice(0, 7) : t("noBase")}</b>
            </p>
            <p className="small muted tabular" style={{ margin: 0 }}>
              {t("totals", { sagas: Number(data.sagas), games: Number(data.games), images: Number(data.images) })}
            </p>
          </>
        )}
        <p className="small" style={{ margin: 0 }}>
          {unpublished.data == null ? t("unpublishedUnknown") : t("unpublished", { count: unpublished.data })}
        </p>
      </section>

      {pending > 0 && (
        <section className="panel" aria-labelledby="catalog-conflicts">
          <h2 id="catalog-conflicts">{t("conflicts", { count: pending })}</h2>
          <p className="small muted" style={{ margin: 0 }}>{t("conflictsHint")}</p>
          <ul style={{ listStyle: "none", padding: 0, margin: 0, display: "grid", gap: 8 }}>
            {conflicts.data!.map((c) => (
              <li key={c.id} className="small">
                <b className="tabular">{c.table} {c.key}</b> · {c.field ?? t("wholeRow")}
                <div className="tabular muted">{t("local")}: {c.local ?? t("missing")}</div>
                <div className="tabular muted">{t("remote", { version: c.version })}: {c.remote ?? t("missing")}</div>
                <div className="sentence">
                  <button className="ot-btn" type="button" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: c.id, useRemote: false })}>{t("keepLocal")}</button>
                  <button className="ot-btn" type="button" disabled={resolve.isPending} onClick={() => resolve.mutate({ id: c.id, useRemote: true })}>{t("useRemote")}</button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="panel" aria-labelledby="catalog-versions">
        <h2 id="catalog-versions">{t("versions")}</h2>
        {page.isPending && <p className="small muted" style={{ margin: 0 }}>{t("loading")}</p>}
        {data && data.versions.length === 0 && <p className="small muted" style={{ margin: 0 }}>{t("noVersions")}</p>}
        {data && data.versions.length > 0 && (
          <ul aria-label={t("versions")} style={{ listStyle: "none", padding: 0, margin: 0, display: "grid", gap: 6 }}>
            {data.versions.map((v) => {
              const images = Number(v.images);
              const summary = [v.changes.length > 0 ? t("changes", { count: v.changes.length }) : "", images > 0 ? t("images", { count: images }) : ""].filter(Boolean).join(" · ");
              return (
                <li key={v.tag} className="version-row" style={{ alignItems: "start", gap: 8 }}>
                  <span aria-hidden="true" style={{ width: "1em" }}>{v.current ? "●" : "○"}</span>
                  <span className="small" style={{ flex: 1 }}>
                    <b className="tabular">{v.tag}</b>
                    <span className="muted"> · {day(v.published)}</span>
                    {v.current && <span className="ot-badge ot-badge-have" style={{ marginLeft: 6 }}>{t("current")}</span>}
                    <br />
                    {v.changes.length > 0 ? (
                      <details>
                        <summary className="muted">{summary}</summary>
                        <ul style={{ margin: 0 }}>
                          {v.changes.map((c) => (
                            <li key={`${c.kind}-${c.id}-${c.change}`}>
                              {dt(`settings.catalog.change.${c.change}`, { kind: dt(`settings.catalog.kind.${c.kind}`), name: c.name ?? "", from: c.from ?? "" })}
                            </li>
                          ))}
                        </ul>
                      </details>
                    ) : <span className="muted">{summary || t("noHistory")}</span>}
                  </span>
                  {!v.current && (
                    <button className="ot-btn" type="button" disabled={apply.isPending || pending > 0} aria-busy={apply.isPending && apply.variables === v.tag}
                      onClick={() => apply.mutate(v.tag)}>{t("apply")}</button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
        {pending > 0 && <p className="small muted" style={{ margin: 0 }}>{t("chooseFirst")}</p>}
        {apply.data && (
          <p className="small" role="status" style={{ margin: 0 }}>
            {t("applied", {
              version: apply.data.tag, inserted: Number(apply.data.inserted), updated: Number(apply.data.updated),
              deleted: Number(apply.data.deleted), conflicts: Number(apply.data.conflicts),
            })}
          </p>
        )}
        {error && <p className="error small" role="alert" style={{ margin: 0 }}>{describe(error)}</p>}
      </section>
    </div>
  );
}
