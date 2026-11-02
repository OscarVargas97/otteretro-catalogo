"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { SagaTile, useConsoles } from "@/components/Catalog";
import { NewSaga } from "@/components/SagaEditing";
import Link from "next/link";
import { Empty } from "@/components/Shell";
import { useDescribeError } from "@/lib/i18n";
import { Tiles } from "@/components/Skeleton";
import { usePaged } from "@/lib/paged";
import { PublishButton } from "@/components/Publish";
import { sagasUrl } from "@/lib/queries";
import type { SagaPage } from "@/lib/types";

const FILTERS = ["all", "iconic", "exclusive", "pc"] as const;
const COMPANIES = ["Nintendo", "Sony", "Microsoft"];
const SORTS = ["recognized", "name", "played", "activity"] as const;

/**
 * Sagas (§3.1): cuadrícula con filtros y orden. «Emulación» va siempre al final. El filtro y la plataforma (empresa o consola)
 * se combinan: «Exclusivas» + NES son las exclusivas de Nintendo que están en NES, y «Más reconocidas» ordena por lo de esa consola.
 */
export default function Sagas() {
  const t = useTranslations("catalog.sagas");
  const tc = useTranslations("catalog");
  const describe = useDescribeError();
  const consoles = useConsoles();
  const [filter, setFilter] = useState<string>("all");
  const [where, setWhere] = useState(""); // "", "company:Nintendo" o "console:nes"
  const [sort, setSort] = useState<(typeof SORTS)[number]>("recognized");
  // Scroll infinito por tramos (#96, #177): el servidor filtra y ordena, y manda de a 60.
  const sagas = usePaged(["sagas", filter, where, sort], (offset) => sagasUrl(filter, where, sort, offset), (p: SagaPage) => ({ items: p.items, total: Number(p.total) }));
  const view = `${filter}|${where}|${sort}`;
  useEffect(() => { window.scrollTo({ top: 0 }); }, [view]); // otro filtro u orden: desde el principio

  const chip = (id: string, label: string) => (
    <button key={id} className="chip" type="button" aria-pressed={filter === id} onClick={() => setFilter(id)}>{label}</button>
  );
  const nothingLoaded = filter === "all" && where === "" && !!sagas.data && sagas.total === 0;

  return (
    <div className="content">
      {!nothingLoaded && (
        // Fija bajo la barra de arriba, con el total (#170).
        <div className="toolbar toolbar-sticky">
          <div className="chips" role="group" aria-label={t("filters")}>
            {FILTERS.map((f) => chip(f, t(`filter.${f}`)))}
          </div>
          <select className="ot-btn" aria-label={t("platform")} value={where} onChange={(e) => setWhere(e.target.value)}>
            <option value="">{t("allPlatforms")}</option>
            {COMPANIES.map((c) => <option key={c} value={`company:${c}`}>{c}</option>)}
            {consoles.data?.filter((c) => Number(c.games) > 0).map((c) => <option key={c.id} value={`console:${c.id}`}>{c.name}</option>)}
          </select>
          <label className="small muted toolbar-label" htmlFor="sort">
            {t("sortBy")}
            <select id="sort" className="ot-btn" value={sort} onChange={(e) => setSort(e.target.value as typeof sort)}>
              {SORTS.map((s) => <option key={s} value={s}>{t(`sort.${s}`)}</option>)}
            </select>
          </label>
          <NewSaga />
          {/* #41: lo que cambió una tarea grande (por ejemplo, completar imágenes) va en un solo PR. */}
          <PublishButton kind="all" id={0} />
          <span className="small muted tabular toolbar-total" aria-live="polite">{sagas.data ? t("total", { count: sagas.total }) : ""}</span>
        </div>
      )}
      {sagas.isError && <p className="error small" role="alert">{describe(sagas.error)}</p>}
      {nothingLoaded && <Empty title={t("emptyTitle")} text={t("emptyText")}><Link className="ot-btn ot-btn-primary" href="/settings/?s=credentials">{tc("openCredentials")}</Link></Empty>}
      {!nothingLoaded && sagas.data && sagas.total === 0 && <p className="muted">{t("noMatch")}</p>}
      <div className="grid-cards">
        {sagas.items.map((s) => <SagaTile key={s.id} saga={s} />)}
        {(!sagas.data || sagas.hasNextPage) && <Tiles kind="saga" count={sagas.data ? 4 : 12} />}
      </div>
      <div ref={sagas.end} aria-hidden="true" />
    </div>
  );
}
