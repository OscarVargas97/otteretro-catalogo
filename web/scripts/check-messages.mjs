// Falla si algún catálogo de messages/ no tiene exactamente las mismas claves que es.json (#141).
// TypeScript ya avisa si falta una; esto avisa también si sobra.
import { readdirSync, readFileSync } from "node:fs";

const keys = (o, prefix = "") =>
  Object.entries(o).flatMap(([k, v]) => (typeof v === "object" ? keys(v, `${prefix}${k}.`) : [`${prefix}${k}`]));
const read = (f) => new Set(keys(JSON.parse(readFileSync(`messages/${f}`, "utf8"))));

const base = read("es.json");
let failed = false;
for (const file of readdirSync("messages").filter((f) => f.endsWith(".json") && f !== "es.json")) {
  const other = read(file);
  const missing = [...base].filter((k) => !other.has(k));
  const extra = [...other].filter((k) => !base.has(k));
  if (missing.length || extra.length) {
    failed = true;
    console.error(`${file}: faltan ${missing.join(", ") || "—"}; sobran ${extra.join(", ") || "—"}`);
  }
}
process.exit(failed ? 1 : 0);
