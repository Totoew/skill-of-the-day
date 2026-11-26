import { test, expect } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { administrator, remove, sql } from "../../backend/Services/SkillService/tests/helpers.mjs";
import { password } from "../../auth-service/tests/helpers.mjs";

test.describe("Real SkillService JSON import", () => {
  test.skip(!process.env.SKILL_INTEGRATION, "Run with SKILL_INTEGRATION=1 against Docker Compose");
  test("An administrator checks a file, imports it, exports it and saves the new skill in a profile", async ({ page }) => {
    const admin = await administrator();
    const code = "browser-import-" + randomBytes(5).toString("hex"), name = "Навык " + code;
    try {
      await page.goto("/#/login");
      await page.getByLabel("Электронная почта", { exact: true }).fill(admin.email);
      await page.getByLabel("Пароль", { exact: true }).fill(password);
      await page.getByRole("button", { name: "Войти", exact: true }).click();
      await expect(page.getByRole("heading", { name: "Ваш roadmap", exact: true })).toBeVisible();
      await page.goto("/#/admin");
      const panel = page.locator(".catalog-import"), input = panel.locator('input[type="file"]');
      await input.setInputFiles({ name: "broken.json", mimeType: "application/json", buffer: Buffer.from("{wrong") });
      await expect(panel.getByRole("alert")).toContainText("Не удалось прочитать JSON");
      await expect(panel.getByRole("button", { name: "Проверить файл" })).toBeDisabled();
      await input.setInputFiles({ name: "invalid.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify({ schemaVersion: 1, skills: [] })) });
      await panel.getByRole("button", { name: "Проверить файл" }).click();
      await expect(panel.getByRole("alert")).toContainText("от 1 до 2000");
      await expect(panel.getByRole("button", { name: "Импортировать справочник" })).toHaveCount(0);
      const catalog = { schemaVersion: 1, skills: [{ code, name, synonyms: [code + " alias"], directions: ["analyst"], basicFor: ["analyst"] }] };
      await input.setInputFiles({ name: "skills.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify(catalog)) });
      await panel.getByRole("button", { name: "Проверить файл" }).click();
      await expect(panel.locator(".catalog-preview")).toContainText("Будет добавлено: 1");
      await panel.getByRole("button", { name: "Импортировать справочник" }).click();
      await expect(panel.getByRole("status").filter({ hasText: "Импорт завершён." })).toContainText("Добавлено: 1");
      const exported = page.waitForEvent("download");
      await panel.getByRole("button", { name: "Выгрузить справочник" }).click();
      expect((await exported).suggestedFilename()).toBe("skill-catalog.json");
      await page.screenshot({ path: "/tmp/skill-day-catalog-import-desktop.png", fullPage: true });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({ path: "/tmp/skill-day-catalog-import-mobile.png", fullPage: true });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.goto("/#/profile");
      const basic = page.getByRole("group", { name: "Базовые навыки", exact: true });
      await expect(basic.getByLabel(name, { exact: true })).toBeVisible();
      await basic.getByLabel(name, { exact: true }).check();
      await page.getByRole("button", { name: "Сохранить изменения", exact: true }).click();
      await expect(page.getByRole("status")).toContainText("Параметры сохранены на сервере");
      await page.reload();
      await expect(page.getByLabel(name, { exact: true })).toBeChecked();
    } finally {
      sql(`BEGIN; SELECT pg_advisory_xact_lock(72619112501); DELETE FROM skill.skills WHERE code='${code}'; UPDATE skill.catalog_state SET version=version+1 WHERE id=1; DELETE FROM skill.catalog_imports WHERE actor_id='${admin.id}'; COMMIT;`);
      await remove(admin.client);
    }
  });
});
