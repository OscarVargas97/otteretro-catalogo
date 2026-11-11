"use client";

import { useTranslations } from "next-intl";
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";

type Tab = { id: string; label: ReactNode };

/**
 * Pestañas subrayadas en una sola línea (#108): nunca barra de desplazamiento ni segunda línea. Las que no caben pasan a
 * «Más ▾» al final, como en Fluent 2 y Primer; la elegida siempre queda a la vista. Flechas, Inicio y Fin mueven entre ellas.
 * `panel` es el id del panel que controlan; cada pestaña lleva el id `tab-<id>`.
 */
export function Tabs({ tabs, current, onSelect, label, panel }: { tabs: Tab[]; current: string; onSelect: (id: string) => void; label: string; panel: string }) {
  const t = useTranslations("tabs");
  const nav = useRef<HTMLDivElement>(null);
  const ruler = useRef<HTMLDivElement>(null);
  const [fit, setFit] = useState(tabs.length);
  const [open, setOpen] = useState(false);

  // Cuántas caben: se mide una copia invisible con todas (y el botón «Más») contra el ancho de la fila.
  useLayoutEffect(() => {
    const measure = () => {
      if (!nav.current || !ruler.current) return;
      const widths = [...ruler.current.children].map((c) => (c as HTMLElement).offsetWidth);
      const more = widths.pop()!;
      const room = nav.current.clientWidth;
      const gap = 4;
      let used = 0;
      let n = 0;
      for (const w of widths) {
        if (used + w > room) break;
        used += w + gap;
        n++;
      }
      if (n < widths.length) { // hace falta «Más»: se le hace lugar
        while (n > 0 && widths.slice(0, n).reduce((a, w) => a + w + gap, 0) + more > room) n--;
      }
      setFit(n);
    };
    measure();
    const watch = new ResizeObserver(measure);
    watch.observe(nav.current!);
    return () => watch.disconnect();
  }, [tabs]);

  const at = tabs.findIndex((x) => x.id === current);
  let shown = tabs.slice(0, fit);
  if (at >= fit && fit > 0) shown = [...tabs.slice(0, fit - 1), tabs[at]]; // la elegida, siempre a la vista
  const hidden = tabs.filter((x) => !shown.includes(x));

  const onKeyDown = (e: React.KeyboardEvent) => {
    const i = shown.findIndex((x) => x.id === current);
    const to = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: shown.length - 1 }[e.key];
    if (to === undefined || !(e.target as HTMLElement).matches('[role="tab"]')) return;
    e.preventDefault();
    const next = shown[(to + shown.length) % shown.length];
    onSelect(next.id);
    document.getElementById(`tab-${next.id}`)?.focus();
  };

  const select = (id: string) => { setOpen(false); onSelect(id); };

  return (
    <div className="tabs-row">
      <div ref={nav} className="tabs tabs-line" role="tablist" aria-label={label} onKeyDown={onKeyDown}>
        {shown.map((x) => (
          <button key={x.id} id={`tab-${x.id}`} type="button" className="tab" role="tab" aria-selected={x.id === current} aria-controls={panel}
            tabIndex={x.id === current ? 0 : -1} onClick={() => select(x.id)}>
            {x.label}
          </button>
        ))}
        {hidden.length > 0 && (
          <div className="tabs-more">
            <button type="button" className="tab" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}
              onKeyDown={(e) => { if (e.key === "Escape") setOpen(false); }}>
              {t("more")} ▾
            </button>
            {open && (
              <>
                <div className="tabs-more-scrim" onClick={() => setOpen(false)} />
                <div className="tabs-more-menu" role="menu" onKeyDown={(e) => { if (e.key === "Escape") setOpen(false); }}>
                  {hidden.map((x) => (
                    <button key={x.id} type="button" role="menuitem" className="tabs-more-item" onClick={() => select(x.id)} autoFocus={x === hidden[0]}>{x.label}</button>
                  ))}
                </div>
              </>
            )}
          </div>
        )}
      </div>
      {/* Para medir: las mismas pestañas, invisibles y fuera del flujo. */}
      <div ref={ruler} className="tabs tabs-line tabs-ruler" aria-hidden="true">
        {tabs.map((x) => <span key={x.id} className="tab">{x.label}</span>)}
        <span className="tab">{t("more")} ▾</span>
      </div>
    </div>
  );
}
