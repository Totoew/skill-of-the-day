import { test, expect } from "@playwright/test";
import { account, password } from "../../auth-service/tests/helpers.mjs";

test.describe("Real RecommendationService through Gateway", () => {
  test.skip(!process.env.RECOMMENDATION_INTEGRATION, "Run with RECOMMENDATION_INTEGRATION=1 against Docker Compose");
  test("The server supplies the graph, table, evidence, deficit filtering and manual recalculation", async ({ page }) => {
    const { client, email } = await account();
    try {
      const saved = await page.request.put("/api/profile/me", { headers: { Authorization: `Bearer ${client.token}` }, data: {
        direction: "analyst", level: "all", regions: ["Москва", "Удалённая работа"], skills: ["Анализ требований", "Моделирование данных", "UML"], version: 0 } });
      expect(saved.status()).toBe(200);
      await page.goto("/#/login");
      await page.getByLabel("Электронная почта", { exact: true }).fill(email);
      await page.getByLabel("Пароль", { exact: true }).fill(password);
      await page.getByRole("button", { name: "Войти", exact: true }).click();
      await expect(page.locator(".metrics")).toContainText("Охват · 24 из 120");
      await expect(page.locator(".scope-line")).toContainText("Дата среза: 20.10.2026");
      await page.getByRole("button", { name: "Git", exact: true }).hover();
      await expect(page.getByRole("tooltip")).toContainText("Вклад навыка: +24");
      await page.getByRole("button", { name: "Git", exact: true }).click();
      await expect(page.getByRole("dialog").locator(".vacancy-example")).toHaveCount(24);
      await expect(page.getByRole("dialog").locator(".coverage-comparison")).toContainText("96 из 120 (80%)");
      await page.keyboard.press("Escape");
      await page.getByRole("button", { name: "Слой 3 · 46", exact: true }).click();
      await expect(page.locator(".metrics")).toContainText("Охват · 0 из 46");
      await page.getByRole("tab", { name: "Таблица", exact: true }).click();
      for (const [index, gain] of ["+24", "+14", "+8"].entries()) await expect(page.locator(".plan-table-row").nth(index)).toContainText(gain);
      const recalculated = page.waitForResponse(response => response.url().includes("/api/recommendations/recalculate"));
      await page.getByRole("button", { name: "Пересчитать план", exact: true }).click();
      expect((await recalculated).status()).toBe(200);
      await expect(page.getByRole("button", { name: "Пересчитать план", exact: true })).toBeEnabled();
      await page.setViewportSize({ width: 390, height: 844 });
      await page.getByRole("tab", { name: "Граф", exact: true }).click();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: "/tmp/skill-day-recommendation-mobile.png", fullPage: true });
    } finally { await client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 }); }
  });
});
