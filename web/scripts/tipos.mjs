// Genera lib/api.d.ts desde el contrato OpenAPI de la API (A3).
// `dotnet run` ejecuta desde la carpeta del proyecto, así que se le pasa una ruta completa.
import { execFileSync, execSync } from "node:child_process";
import { resolve } from "node:path";

const contrato = resolve("lib/openapi.json");
execFileSync("dotnet", ["run", "--project", "../src/Otteretro", "--", "openapi", contrato], { stdio: "inherit" });
execSync(`npx openapi-typescript "${contrato}" -o lib/api.d.ts`, { stdio: "inherit" });
