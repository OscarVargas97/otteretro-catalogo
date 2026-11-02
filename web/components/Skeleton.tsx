/**
 * Esqueletos (#105): la forma de lo que llega, con las mismas clases y del mismo tamaño, para que nada salte al llegar.
 * El texto de relleno (espacios de cifra) da la altura de línea real y no se ve.
 */
const Text = ({ chars = 12, className = "" }: { chars?: number; className?: string }) => (
  <span className={`ot-skel ${className}`}>{" ".repeat(chars)}</span>
);

export function SagaTileSkeleton() {
  return (
    <div className="ot-saga" aria-hidden="true">
      <div className="ot-saga-art ot-skel" />
      <div className="ot-saga-body">
        <div className="ot-saga-badges"><Text className="ot-badge" chars={8} /></div>
        <div className="ot-saga-stats"><Text chars={9} /><Text chars={5} /></div>
        <div className="progress-line"><div className="ot-progress" /><Text className="small" chars={6} /></div>
        <div className="chips" style={{ gap: 4 }}><Text className="ot-console" chars={5} /><Text className="ot-console" chars={5} /></div>
      </div>
    </div>
  );
}

export function GameTileSkeleton() {
  return (
    <div className="ot-game" aria-hidden="true">
      <div className="ot-cover ot-skel" />
      <p className="ot-game-name"><Text chars={16} /></p>
      <div className="ot-game-meta"><Text className="ot-console" chars={5} /><Text chars={4} /></div>
      <div className="ot-game-badges"><Text className="ot-badge" chars={7} /></div>
    </div>
  );
}

/** `count` tarjetas de saga o de juego, dentro de la misma cuadrícula que las reales. */
export function Tiles({ kind, count }: { kind: "saga" | "game"; count: number }) {
  const Tile = kind === "saga" ? SagaTileSkeleton : GameTileSkeleton;
  return <>{Array.from({ length: count }, (_, i) => <Tile key={i} />)}</>;
}

/** Filas de una tabla `t` con `cols` columnas. */
export function Rows({ cols, count }: { cols: number; count: number }) {
  return <>{Array.from({ length: count }, (_, i) => (
    <tr key={i} aria-hidden="true">{Array.from({ length: cols }, (_, j) => <td key={j}><Text chars={j === 0 ? 18 : 6} /></td>)}</tr>
  ))}</>;
}

/** Bloques de panel (Revisar, Cuentas y claves): una línea de título y dos de texto. */
export function Panels({ count }: { count: number }) {
  return <>{Array.from({ length: count }, (_, i) => (
    <div key={i} className="panel" aria-hidden="true" style={{ display: "grid", gap: 6 }}><b><Text chars={24} /></b><Text chars={40} /><Text chars={30} /></div>
  ))}</>;
}

/** Tarjetas horizontales de «Siguiente en tus rutas» (#170). */
export function NextCards({ count }: { count: number }) {
  return <div className="next-list">{Array.from({ length: count }, (_, i) => (
    <div key={i} className="next-card" aria-hidden="true">
      <div className="ot-cover ot-skel" />
      <div className="next-body"><b className="ot-game-name"><Text chars={18} /></b><Text className="small" chars={24} /><div className="progress-line"><div className="ot-progress" /><Text className="small" chars={6} /></div></div>
    </div>
  ))}</div>;
}

/** Cabecera de Saga o Juego (con portada en Juego) y la tabla de abajo, mientras llega la ficha. */
export function PageSkeleton({ cover = false }: { cover?: boolean }) {
  return (
    <div aria-busy="true">
      <section className="hero">
        {cover && <div className="ot-cover ot-skel" style={{ width: 150, flex: "none" }} />}
        <div className="hero-main">
          <Text chars={10} />
          <Text chars={24} className="small" />
          <h2 className="hero-title"><Text chars={18} /></h2>
          <div className="hero-actions"><Text className="ot-btn" chars={10} /><Text className="ot-btn" chars={10} /></div>
        </div>
      </section>
      <div className="content">
        <div className="panel table-wrap" style={{ padding: 0 }}><table className="t"><tbody><Rows cols={4} count={8} /></tbody></table></div>
      </div>
    </div>
  );
}
