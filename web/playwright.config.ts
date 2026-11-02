// Pruebas de interfaz (e2e) contra la app real: `otteretro servidor` sirve la API y el front compilado.
// Usan el Chrome instalado (channel: "chrome"), así que no hace falta descargar navegadores.
// Correr con `npm run e2e` (compila el front y el backend antes).
import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "e2e",
  globalSetup: "./e2e/server.ts",
  fullyParallel: false,
  workers: 1,
  reporter: [["list"]],
  use: {
    channel: "chrome",
    viewport: { width: 1440, height: 900 },
    screenshot: "only-on-failure",
  },
  outputDir: "e2e-results",
});
