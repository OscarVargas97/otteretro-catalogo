// Recorre la administración como lo haría la persona, en español e inglés. Las pruebas van en orden y comparten la misma pestaña.
import { expect, test, type Page } from "@playwright/test";
import { join } from "node:path";
import en from "../messages/en.json";
import es from "../messages/es.json";

const url = () => process.env.OTTERETRO_E2E_URL!;
const base = () => url().split("/?")[0];
const dataDir = (name: string) => join(process.env.OTTERETRO_E2E_DIR!, name);

test.describe.configure({ mode: "serial" });

let page: Page;
const problems: string[] = [];

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage();
  page.on("console", (m) => m.type() === "error" && !m.text().startsWith("Failed to load resource") && problems.push(m.text()));
  page.on("pageerror", (e) => problems.push(e.message));
  page.on("response", (r) => {
    const path = new URL(r.url()).pathname;
    if (r.status() >= 400 && !(path.startsWith("/api/") && r.status() < 500)) problems.push(`${r.status()} ${path}`);
  });
});

test.afterEach(() => {
  expect(problems, "errores en la consola").toEqual([]);
});

test("el asistente elige la carpeta de datos y pide las claves del catálogo", async () => {
  await page.goto(url());
  await expect(page.getByRole("button", { name: es.welcome.start })).toBeVisible();
  await page.getByRole("button", { name: es.welcome.start }).click();
  await expect(page.getByRole("radio").first()).toBeVisible();
  await page.getByLabel(es.welcome.otherPath).fill(dataDir("data"));
  await page.getByRole("button", { name: /^Usar / }).click();
  await expect(page.getByRole("heading", { name: es.welcome.accountsTitle })).toBeVisible();
  await page.getByRole("button", { name: es.welcome.done }).click();
  await expect(page).toHaveURL(/\/\?|\/$/);
  await expect(page.getByRole("heading", { name: es.home.review, exact: true })).toBeVisible();
});

test("cada pantalla abre, y lo que era de cada persona ya no está en el menú", async () => {
  const menu = page.locator(".sidebar");
  for (const key of ["home", "sagas", "review", "settings"] as const) await expect(menu.getByRole("link", { name: es.nav[key] })).toBeVisible();
  await expect(menu.locator("a.ot-nav-item")).toHaveCount(4);
  for (const path of ["sagas", "review", "settings"]) {
    await page.goto(`${base()}/${path}/`);
    await expect(page.locator(".top h1")).toBeAttached();
  }
});

test("Ajustes tiene claves, consolas, Claude y general", async () => {
  await page.goto(`${base()}/settings/`);
  await expect(page.getByRole("tab")).toHaveCount(4);
  await page.getByRole("tab", { name: es.settings.general }).click();
  await expect(page.getByText(es.settings.attribution)).toBeVisible();
  await page.getByRole("button", { name: en.languageName }).click();
  await expect(page.getByRole("tab", { name: en.settings.general })).toBeVisible();
  await page.getByRole("button", { name: es.languageName }).click();
});
