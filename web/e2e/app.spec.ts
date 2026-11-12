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
  // Sin Otteretro se elige el disco ★ de las imágenes (#9). No se elige: crearía la carpeta en un disco real.
  await expect(page.getByText(es.settings.images.choose)).toBeVisible();
  await expect(page.getByRole("group", { name: es.settings.images.title }).getByRole("button").first()).toBeVisible();
  await page.getByRole("button", { name: es.welcome.done }).click();
  await expect(page).toHaveURL(/\/\?|\/$/);
  await expect(page.getByRole("heading", { name: es.home.review, exact: true })).toBeVisible();
});

test("cada pantalla abre, y lo que era de cada persona ya no está en el menú", async () => {
  const menu = page.locator(".sidebar");
  for (const key of ["home", "sagas", "review", "emulators", "settings"] as const) await expect(menu.getByRole("link", { name: es.nav[key] })).toBeVisible();
  await expect(menu.locator("a.ot-nav-item")).toHaveCount(5);
  for (const path of ["sagas", "review", "emulators", "settings"]) {
    await page.goto(`${base()}/${path}/`);
    await expect(page.locator(".top h1")).toBeAttached();
  }
});

test("se crea una saga, se le agregan juegos, se arma su ruta y se fusiona un juego (#17)", async () => {
  // Dos juegos sin saga, como los deja una carga de IGDB.
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(join(dataDir("data"), "catalogo", "catalogo.db"));
  db.exec(`INSERT INTO game (id, saga_id, name_es, year) VALUES (500, NULL, 'Secret of Mana', 1993), (501, NULL, 'Trials of Mana', 1995);
           INSERT INTO version (id, game_id, platform, kind, year) VALUES (600, 500, 'snes', 'original', 1993), (601, 501, 'snes', 'original', 1995);`);
  db.close();
  const edit = es.catalog.edit;

  await page.goto(`${base()}/sagas/`);
  await page.getByRole("button", { name: edit.newSaga }).click();
  await page.getByLabel(edit.newSagaName).fill("Mana");
  await page.getByRole("button", { name: edit.create }).click();
  await expect(page.getByRole("heading", { name: "Mana", level: 2 })).toBeVisible();

  for (const name of ["Secret of Mana", "Trials of Mana"]) {
    await page.getByLabel(edit.searchGame).fill("of Mana");
    await page.getByRole("button", { name: `${edit.addGame}: ${name}` }).click();
    await expect(page.getByRole("link", { name })).toBeVisible();
  }

  await page.getByRole("button", { name: edit.routeCreate.replace("{kind}", es.catalog.route.release) }).click();
  await page.getByRole("button", { name: edit.addStep }).click();
  await page.getByRole("button", { name: edit.addStep }).click();
  await page.getByLabel(edit.game).nth(1).selectOption({ label: "Trials of Mana" });
  await page.getByLabel(edit.version).nth(1).selectOption({ index: 1 });
  await page.getByRole("button", { name: edit.save, exact: true }).last().click();
  const steps = page.locator(".timeline li");
  await expect(steps).toHaveCount(2);
  await expect(steps.nth(1)).toContainText("Trials of Mana");

  await page.getByLabel(edit.name).fill("Saga de Mana");
  await page.getByRole("button", { name: edit.save, exact: true }).first().click();
  await expect(page.getByRole("heading", { name: "Saga de Mana", level: 2 })).toBeVisible();

  const trials = page.getByRole("row", { name: /Trials of Mana/ });
  await trials.getByLabel(edit.actions).selectOption("merge");
  await trials.getByLabel(edit.mergeInto).selectOption({ label: "Secret of Mana" });
  await trials.getByRole("button", { name: edit.confirm }).click();
  await expect(page.getByRole("row", { name: /Trials of Mana/ })).toHaveCount(0);
  // El paso de la ruta pasó al juego que quedó.
  await expect(steps.nth(1)).toContainText("Secret of Mana");

  // Publicar sin clave de GitHub dice qué token crear y lleva a Cuentas y claves (#22).
  await page.getByRole("button", { name: es.publish.button, exact: true }).click();
  await expect(page.getByText(es.publish.noKey)).toBeVisible();
  await expect(page.getByRole("link", { name: es.publish.openCredentials })).toBeVisible();
  await page.getByRole("button", { name: es.publish.close }).click();
});

test("el mejor emulador se busca solo en las sagas y juegos elegidos, y dice cuántos son (#133)", async () => {
  const { DatabaseSync } = await import("node:sqlite");
  let db = new DatabaseSync(join(dataDir("data"), "catalogo", "catalogo.db"));
  db.exec("INSERT INTO game (id, saga_id, name_es, year) SELECT 502, id, 'Legend of Mana', 1999 FROM saga WHERE name = 'Saga de Mana'");
  db.close();
  db = new DatabaseSync(join(dataDir("data"), "data", "otteretro.db")); // la fecha de la búsqueda va en la base propia
  db.exec("INSERT INTO emulator_search (game_id, searched_at) VALUES (500, '2026-01-02T12:00:00Z')");
  db.close();
  const t = es.bestEmulator;

  await page.goto(`${base()}/sagas/`);
  await page.getByRole("button", { name: t.button }).click();
  const dialog = page.getByRole("dialog", { name: t.title });
  await expect(dialog.getByRole("checkbox")).toHaveCount(0); // nada elegido: nada que buscar
  await expect(dialog.getByRole("button", { name: t.start, exact: true })).toBeDisabled();
  await dialog.getByLabel(t.add).fill("Saga de Mana");
  await dialog.getByRole("button", { name: /^Saga de Mana/ }).click();
  // El ya buscado viene desmarcado y con su fecha; el otro, marcado.
  await expect(dialog.getByRole("checkbox", { name: /Secret of Mana/ })).not.toBeChecked();
  await expect(dialog.getByRole("checkbox", { name: /Legend of Mana/ })).toBeChecked();
  await expect(dialog.getByText(t.never)).toHaveCount(1);
  await expect(dialog.getByText("Se buscará en 1 juego")).toBeVisible();
  await dialog.getByRole("checkbox", { name: /Secret of Mana/ }).check();
  await expect(dialog.getByText("Se buscará en 2 juegos")).toBeVisible();
  await dialog.getByRole("button", { name: t.close }).click(); // sin encolar: llamaría a Claude de verdad
  await expect(dialog).toBeHidden();
});

test("una receta se edita por secciones, con chips y el JSON solo en la vista avanzada plegada (#111)", async () => {
  const { DatabaseSync } = await import("node:sqlite");
  const file = join(dataDir("data"), "catalogo", "catalogo.db");
  let db = new DatabaseSync(file);
  db.exec(`INSERT INTO recipe (id, kind, json) VALUES ('prueba', 'emulator', '{"id":"prueba","name":"Prueba","kind":"emulator","folder":"snes","detect":"prueba.exe","formats":["sfc"]}')`);
  db.close();
  const t = es.emulators;

  await page.goto(`${base()}/emulators/`);
  await page.getByRole("tab", { name: new RegExp(`^${t.tabs.emulator}`) }).click();
  await page.getByRole("button", { name: "Prueba", exact: true }).click();
  await page.getByRole("button", { name: t.card.edit }).click();
  await expect(page.getByRole("tab", { name: t.sections.basic })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByLabel(t.field.name, { exact: true })).toHaveValue("Prueba");
  await expect(page.locator("textarea")).toBeHidden();

  await page.getByRole("tab", { name: t.sections.consoles }).click();
  const formats = page.getByRole("textbox", { name: t.field.formats });
  await formats.fill("smc");
  await formats.press("Enter");
  await expect(page.getByRole("button", { name: t.removeChip.replace("{value}", "smc") })).toBeVisible();

  await page.getByText(t.jsonView).click();
  await expect(page.locator("textarea")).toHaveValue(/"smc"/);
  await page.getByRole("button", { name: t.save, exact: true }).click();
  await expect(page.getByRole("tab", { name: t.sections.basic })).toHaveCount(0);

  db = new DatabaseSync(file);
  const saved = db.prepare("SELECT json FROM recipe WHERE id = 'prueba'").get() as { json: string };
  db.close();
  expect(JSON.parse(saved.json).formats).toEqual(["sfc", "smc"]);
});

test("Programas: una pestaña por tipo y Agregar en 3 pasos, con el archivo detectado y los demás campos plegados (#166)", async () => {
  const t = es.programs;
  const e = es.emulators;
  // Sin red: las releases, el borrador y el guardado (que mira la fuente en GitHub) se responden acá.
  const files = [{ name: "azahar-2121-macos.zip", windows: false, pattern: "" }, { name: "azahar-2121-windows-msvc.zip", windows: true, pattern: "^azahar-[0-9.]+-windows-msvc\.zip$" }];
  await page.route("**/api/recipes/releases?*", (r) => r.fulfill({ json: [{ tag: "2121", publishedAt: null, files }] }));
  await page.route("**/api/recipes/draft", (r) => r.fulfill({ json: { json: JSON.stringify({ id: "azahar-e2e", name: "azahar", kind: "emulator", folder: "", detect: "azahar.exe", consoles: [], updates: { source: "github", repo: "azahar-emu/azahar" }, asset: "^azahar-[0-9.]+-windows-msvc\.zip$", file: JSON.parse(r.request().postData()!).file }) } }));
  let saved: Record<string, unknown> | null = null;
  await page.route("**/api/recipes/azahar-e2e?create=true", (r) => { saved = JSON.parse(JSON.parse(r.request().postData()!).json); return r.fulfill({ json: [] }); });

  await page.goto(`${base()}/emulators/`);
  await expect(page.locator(".top h1")).toHaveText(es.nav.emulators);
  await expect(page.getByRole("tab")).toHaveCount(4);
  await expect(page.getByRole("tab", { name: new RegExp(`^${e.tabs.port}`) })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText(e.emptyTab)).toBeVisible(); // sin ports en la base de prueba
  await page.getByRole("tab", { name: new RegExp(`^${e.tabs.emulator}`) }).click();
  await expect(page.getByRole("columnheader", { name: t.kind.emulator.column })).toBeVisible();

  await page.getByRole("button", { name: t.add }).click();
  await expect(page.getByRole("button", { name: new RegExp(e.kinds.emulator) })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("button", { name: t.wizard.next })).toBeDisabled();
  await page.getByLabel(t.wizard.repo).fill("https://github.com/azahar-emu/azahar");
  await page.getByLabel(t.wizard.repo).press("Enter");
  await expect(page.getByLabel(t.wizard.file)).toHaveValue("azahar-2121-windows-msvc.zip"); // el de Windows, solo
  await page.getByLabel(e.field.name, { exact: true }).fill("Azahar");
  await page.getByRole("button", { name: t.wizard.next }).click();

  await expect(page.getByRole("heading", { name: t.kind.emulator.question })).toBeVisible();
  await expect(page.getByRole("button", { name: t.wizard.next })).toBeDisabled();
  await page.getByRole("group", { name: e.field.consoles }).getByRole("button").first().click();
  await expect(page.getByText(t.wizard.formats)).toBeVisible();
  await page.getByRole("button", { name: t.wizard.next }).click();

  await expect(page.getByRole("heading", { name: t.wizard.review })).toBeVisible();
  await expect(page.getByRole("tab", { name: e.sections.basic })).toBeHidden(); // los demás campos, plegados
  await page.getByText(t.wizard.more).click();
  await expect(page.getByLabel(e.field.detect)).toHaveValue("azahar.exe");
  await page.getByRole("button", { name: e.save, exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Azahar" })).toContainText(e.tabs.emulator);
  expect(saved).toMatchObject({ id: "azahar-e2e", name: "Azahar", kind: "emulator" });
  expect((saved as unknown as { consoles: string[] }).consoles).toHaveLength(1);
  await page.unrouteAll();
});

test("Ajustes tiene claves, consolas, tareas y general", async () => {
  await page.goto(`${base()}/settings/`);
  await expect(page.getByRole("tab")).toHaveCount(5); // con Catálogo (#105)
  // #58: la pestaña Claude pasó a ser Tareas (el enlace viejo lleva ahí), con cada tarea en su grupo y su «Ejecutar».
  await page.goto(`${base()}/settings/?s=claude`);
  await expect(page.getByRole("tab", { name: es.settings.claude.title })).toHaveAttribute("aria-selected", "true");
  const group = (name: string) => page.locator("section.panel", { has: page.getByRole("heading", { name, exact: true }) });
  // #77: cada tarea es una fila plegada; el interruptor aparece al expandirla (cuando las filas ya llegaron).
  await expect(group(es.settings.claude.groupClaude).getByText(es.settings.claude.task.routes.name, { exact: true })).toBeVisible();
  await page.locator("section.panel details").evaluateAll((all) => all.forEach((d) => d.setAttribute("open", "")));
  await expect(group(es.settings.claude.groupClaude).getByRole("switch")).toHaveCount(7); // con los ports (#118)
  await expect(group(es.settings.claude.groupScript).getByRole("switch")).toHaveCount(4);
  // #83: todos los «Ejecutar» con el mismo estilo.
  await expect(page.locator("section.panel button.ot-btn-primary", { hasText: es.settings.claude.run })).toHaveCount(0);
  await page.getByRole("tab", { name: es.settings.general }).click();
  await expect(page.getByText(es.settings.attribution)).toBeVisible();
  await page.getByRole("button", { name: en.languageName }).click();
  await expect(page.getByRole("tab", { name: en.settings.general })).toBeVisible();
  await page.getByRole("button", { name: es.languageName }).click();
});

test("Revisar es una bandeja: A, R y S con el teclado, Deshacer y «Aprobar las N» por tipo (#164)", async () => {
  const { DatabaseSync } = await import("node:sqlite");
  let db = new DatabaseSync(join(dataDir("data"), "catalogo", "catalogo.db"));
  db.exec("INSERT INTO game (id, saga_id, name_es, year) VALUES (503, NULL, 'Sword of Mana', 2003)");
  db.close();
  const own = join(dataDir("data"), "data", "otteretro.db");
  db = new DatabaseSync(own);
  const now = new Date().toISOString();
  for (const [id, game] of [[9001, 500], [9002, 502], [9003, 503]])
    db.exec(`INSERT INTO review_item (id, kind, entity, entity_id, data, created_at) VALUES (${id}, 'pending_approval', 'game', ${game},
      '{"prompt":"traducir-descripcion","result":{"text":"Texto ${id}"}}', '${now}')`);
  db.close();
  const status = () => {
    const d = new DatabaseSync(own);
    const rows = d.prepare("SELECT id, status FROM review_item WHERE id >= 9001 ORDER BY id").all() as { id: number; status: string }[];
    d.close();
    return rows.map((r) => r.status);
  };
  const t = es.review;

  await page.goto(`${base()}/review/`);
  const rows = page.getByRole("list", { name: t.list }).getByRole("button");
  const detail = page.getByRole("region", { name: t.detail }).getByRole("heading", { level: 2 });
  await expect(rows).toHaveCount(3);
  await expect(detail).toHaveText("Secret of Mana");
  await page.keyboard.press("s");
  await expect(detail).toHaveText("Legend of Mana");
  await page.keyboard.press("ArrowUp");
  await expect(detail).toHaveText("Secret of Mana");
  await page.keyboard.press("ArrowDown");

  // Aprobar con A pasa al siguiente; Deshacer lo trae de vuelta sin haber tocado el catálogo.
  await page.keyboard.press("a");
  await expect(page.getByRole("status").filter({ hasText: "Aprobado: Legend of Mana." })).toBeVisible();
  await expect(rows).toHaveCount(2);
  await expect(detail).toHaveText("Sword of Mana");
  await page.getByRole("button", { name: t.undo }).click();
  await expect(rows).toHaveCount(3);
  await expect(detail).toHaveText("Legend of Mana");
  expect(status()).toEqual(["open", "open", "open"]);

  // R rechaza; al hacer lo siguiente, lo anterior se manda.
  await page.keyboard.press("r");
  await expect(rows).toHaveCount(2);
  await page.getByRole("group", { name: t.filterType }).getByRole("button", { name: /^Descripción en español/ }).click();
  await page.getByRole("button", { name: t.approveType.replace("{count}", "2").replace("{type}", t.prompt["traducir-descripcion"]) }).click();
  await expect(page.getByRole("heading", { name: t.emptyTitle })).toBeVisible();
  await expect.poll(status).toEqual(["open", "rejected", "open"]);

  // Al salir de la pantalla también se manda lo que esperaba.
  await page.getByRole("link", { name: es.nav.history }).click();
  await expect(page.locator(".top h1")).toHaveText(es.nav.history);
  await expect.poll(status).toEqual(["approved", "rejected", "approved"]);
});

test("el Historial de cambios agrupa por noche y tarea, y deja deshacer y corregir con nota (#164)", async () => {
  const { DatabaseSync } = await import("node:sqlite");
  const file = join(dataDir("data"), "catalogo", "catalogo.db");
  const db = new DatabaseSync(file);
  const saga = (db.prepare("SELECT id FROM saga WHERE name = 'Saga de Mana'").get() as { id: number }).id;
  const now = new Date().toISOString();
  for (const [id, name] of [[9101, "Mana I"], [9102, "Mana II"]])
    db.exec(`INSERT INTO learning (id, kind, content, created_at, task_id) VALUES (${id}, 'rule',
      '{"prompt":"depurar-sagas","change":{"kind":"rename","reason":"Motivo ${id}","sagaId":${saga},"name":"${name}"},"before":{"saga":${saga},"claude":null}}', '${now}', 77)`);
  db.close();
  const t = es.review;

  await page.goto(`${base()}/review/history/`);
  const group = page.locator("section.panel", { has: page.getByText(t.task.replace("{id}", "77")) });
  const changes = group.locator(".change-row");
  await expect(changes).toHaveCount(2);
  // El porqué se ve al pulsar el cambio.
  await expect(changes.nth(0).getByText("Motivo 9102")).toBeHidden();
  await changes.nth(0).getByText(/Mana II/).click();
  await expect(changes.nth(0).getByText("Motivo 9102")).toBeVisible();

  await changes.nth(0).getByRole("button", { name: t.undo }).click();
  await expect(changes.nth(0).getByText(t.undone, { exact: true })).toBeVisible();

  await changes.nth(1).getByRole("button", { name: t.correct }).click();
  await changes.nth(1).getByLabel(t.correct).fill("Mana no lleva número");
  await changes.nth(1).getByRole("button", { name: t.send }).click();
  await expect(changes.nth(1).getByText(t.undone, { exact: true })).toBeVisible();
  await changes.nth(1).getByText(/Mana I/).first().click();
  await expect(changes.nth(1).getByText(t.rule.replace("{text}", "Mana no lleva número"))).toBeVisible();
});
