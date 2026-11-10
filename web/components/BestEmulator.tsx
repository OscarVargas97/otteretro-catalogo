"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useDeferredValue, useState } from "react";
import { ShortPlatforms } from "@/components/Catalog";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { BestEmulatorGame, SearchResults } from "@/lib/types";

type Named = { id: string; name: string };

/**
 * «Buscar el mejor emulador» (#133): un selector propio de sagas y juegos. Nunca busca en todo el catálogo: solo en lo que se agrega acá,
 * y dice cuántos juegos son antes de encolar. Los ya buscados vienen desmarcados, con su fecha.
 */
export function BestEmulatorButton({ saga }: { saga?: Named }) {
  const t = useTranslations("bestEmulator");
  const [open, setOpen] = useState(false);
  return (
    <>
      <button className="ot-btn" type="button" aria-expanded={open} onClick={() => setOpen(true)}>{t("button")}</button>
      {open && <Picker saga={saga} onClose={() => setOpen(false)} />}
    </>
  );
}

function Picker({ saga, onClose }: { saga?: Named; onClose: () => void }) {
  const t = useTranslations("bestEmulator");
  const describe = useDescribeError();
  const format = useFormatter();
  const cache = useQueryClient();
  const [sagas, setSagas] = useState<Named[]>(saga ? [saga] : []);
  const [loose, setLoose] = useState<Named[]>([]);
  const [query, setQuery] = useState("");
  const text = useDeferredValue(query.trim());
  const found = useQuery({
    queryKey: ["search", text],
    queryFn: () => request<SearchResults>(`/api/search?q=${encodeURIComponent(text)}`),
    enabled: text.length >= 2,
  });
  const params = [...sagas.map((s) => `sagas=${s.id}`), ...loose.map((g) => `games=${g.id}`)].join("&");
  const games = useQuery({
    queryKey: ["bestEmulator", params],
    queryFn: () => request<BestEmulatorGame[]>(`/api/best-emulator/games?${params}`),
    enabled: params !== "",
    placeholderData: (previous) => previous,
  });
  // Lo que la persona cambió; el resto sigue la regla: marcado si nunca se buscó.
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const list = params === "" ? [] : games.data ?? [];
  const isPicked = (g: BestEmulatorGame) => picked[String(g.id)] ?? !g.searchedAt;
  const marked = list.filter(isPicked);
  const queue = useMutation({
    mutationFn: () => request<number>("/api/best-emulator", { method: "POST", body: JSON.stringify({ gameIds: marked.map((g) => Number(g.id)) }) }),
    onSuccess: () => cache.invalidateQueries({ queryKey: ["tasks"] }),
  });

  const add = (list: Named[], set: (l: Named[]) => void, item: Named) => {
    if (!list.some((x) => x.id === item.id)) set([...list, item]);
    setQuery("");
  };
  const chip = (item: Named, remove: () => void) => (
    <span key={item.id} className="chip">
      {item.name}
      <button type="button" className="chip-remove" aria-label={t("remove", { name: item.name })} onClick={remove}>×</button>
    </span>
  );

  return (
    <div className="overlay" onClick={onClose}>
      <div className="search-dialog publish-dialog" role="dialog" aria-label={t("title")} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ margin: 0 }}>{t("title")}</h3>
        <div className="publish-body">
          <p className="small muted">{t("intro")}</p>
          <label htmlFor="best-emulator-search" className="small muted">{t("add")}</label>
          <input id="best-emulator-search" value={query} autoComplete="off" placeholder={t("addPlaceholder")} onChange={(e) => setQuery(e.target.value)} />
          {text.length >= 2 && found.data && (
            <div className="search-group">
              {found.data.sagas.map((s) => (
                <button key={`s${s.id}`} type="button" className="search-result link-button" onClick={() => add(sagas, setSagas, { id: String(s.id), name: s.name })}>
                  <span>{s.name}</span><span className="small muted">{t("saga", { count: Number(s.games) })}</span>
                </button>
              ))}
              {found.data.games.map((g) => (
                <button key={`g${g.id}`} type="button" className="search-result link-button" onClick={() => add(loose, setLoose, { id: String(g.id), name: g.name })}>
                  <span>{g.name}</span><span className="small muted">{[g.sagaName, g.year].filter(Boolean).join(" · ")}</span>
                </button>
              ))}
              {found.data.sagas.length + found.data.games.length === 0 && <p className="small muted">{t("noResults")}</p>}
            </div>
          )}
          {sagas.length + loose.length > 0 && (
            <div className="chips">
              {sagas.map((s) => chip(s, () => setSagas(sagas.filter((x) => x !== s))))}
              {loose.map((g) => chip(g, () => setLoose(loose.filter((x) => x !== g))))}
            </div>
          )}
          {games.error && <p className="error small" role="alert">{describe(games.error)}</p>}
          {list.length > 0 && (
            <ul className="detail-list">
              {list.map((g) => (
                <li key={g.id}>
                  <label className="sentence" style={{ flex: 1, minWidth: 0, flexWrap: "nowrap" }}>
                    <input type="checkbox" checked={isPicked(g)} onChange={(e) => setPicked({ ...picked, [String(g.id)]: e.target.checked })} />
                    <span className="grow">
                      <span>{g.name}</span>
                      <span className="small muted">{[g.saga, g.year].filter(Boolean).join(" · ")}</span>
                    </span>
                  </label>
                  <span className="chips" style={{ gap: 4, flexWrap: "nowrap" }}><ShortPlatforms ids={g.platforms} /></span>
                  <span className="small muted">{g.searchedAt ? t("searched", { date: format.dateTime(new Date(g.searchedAt), { dateStyle: "medium" }) }) : t("never")}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
        {queue.data != null ? (
          <div className="sentence" style={{ justifyContent: "flex-end" }}>
            <span className="small" role="status" style={{ flex: 1 }}>{t("queued", { count: queue.data })}</span>
            <button className="ot-btn" type="button" onClick={onClose}>{t("close")}</button>
          </div>
        ) : (
          <div className="sentence" style={{ justifyContent: "flex-end" }}>
            <span className="small muted" role="status" style={{ flex: 1 }}>{t("count", { count: marked.length })}</span>
            <button className="ot-btn ot-btn-ghost" type="button" onClick={onClose}>{t("close")}</button>
            <button className="ot-btn ot-btn-primary" type="button" disabled={marked.length === 0 || queue.isPending} aria-busy={queue.isPending} onClick={() => queue.mutate()}>
              {queue.isPending && <span className="spinner" aria-hidden="true" />}
              {t("start")}
            </button>
          </div>
        )}
        {queue.error && <p className="error small" role="alert">{describe(queue.error)}</p>}
      </div>
    </div>
  );
}
