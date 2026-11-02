// Levanta `otteretro servidor` con una configuración y una carpeta de datos temporales (nunca las reales)
// y deja su dirección, con la clave de la sesión, en OTTERETRO_E2E_URL para las pruebas.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

export default async function startServer() {
  const work = mkdtempSync(join(tmpdir(), "otteretro-e2e-"));
  const config = join(work, "config.json");
  // Idioma fijo para que las pruebas no dependan del sistema.
  writeFileSync(config, JSON.stringify({ language: "es" }));
  process.env.OTTERETRO_E2E_DIR = work;

  const exe = resolve("../src/Otteretro/bin/Debug/net10.0", process.platform === "win32" ? "OtteretroCatalogo.exe" : "OtteretroCatalogo");
  const server = spawn(exe, ["servidor"], { env: { ...process.env, OTTERETRO_CONFIG: config, OTTERETRO_APP_CONFIG: join(work, "sin-otteretro.json"), OTTERETRO_E2E: "1" } });

  process.env.OTTERETRO_E2E_URL = await new Promise<string>((done, fail) => {
    const timer = setTimeout(() => fail(new Error("El servidor no arrancó en 30 s")), 30_000);
    server.stdout.on("data", (chunk: Buffer) => {
      const match = /Abre (\S+)/.exec(chunk.toString());
      if (match) {
        clearTimeout(timer);
        done(match[1]);
      }
    });
    server.on("exit", (code) => fail(new Error(`El servidor terminó con código ${code}`)));
  });

  // Se espera a que el proceso termine: en Windows, borrar antes falla porque todavía tiene abiertos los logs.
  return async () => {
    const exited = new Promise((done) => server.once("exit", done));
    server.kill();
    await exited;
    rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  };
}
