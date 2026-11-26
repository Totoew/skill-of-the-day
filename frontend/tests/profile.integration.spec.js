import { test, expect } from "@playwright/test";
import { account, password } from "../../auth-service/tests/helpers.mjs";

test.describe("Real ProfileService through Gateway", () => {
  test.skip(!process.env.PROFILE_INTEGRATION, "Run with PROFILE_INTEGRATION=1 against Docker Compose");

  async function login(page, email) {
    await page.goto("/#/login");
    await expect(page.getByRole("heading", { name: "С возвращением", exact: true })).toBeVisible();
    await page.getByLabel("Электронная почта", { exact: true }).fill(email);
    await page.getByLabel("Пароль", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Войти", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Ваш roadmap", exact: true })).toBeVisible();
  }
  const save = async page => {
    await page.getByRole("button", { name: "Сохранить изменения", exact: true }).click();
    await expect(page.getByRole("status")).toContainText("Параметры сохранены на сервере");
  };

  test("Settings persist on another device and concurrent changes do not silently overwrite them", async ({ page, browser }) => {
    const { client, email } = await account();
    const device = await browser.newContext();
    try {
      await login(page, email);
      await page.goto("/#/profile");
      await page.getByLabel("Направление", { exact: true }).selectOption("qa");
      await page.getByLabel("Уровень", { exact: true }).selectOption("middle");
      await page.getByLabel("SQL", { exact: true }).check();
      await page.getByLabel("Postman", { exact: true }).check();
      await save(page);
      const other = await device.newPage();
      await login(other, email);
      await other.goto("/#/profile");
      await expect(other.getByLabel("Направление", { exact: true })).toHaveValue("qa");
      await expect(other.getByLabel("Уровень", { exact: true })).toHaveValue("middle");
      await expect(other.getByLabel("SQL", { exact: true })).toBeChecked();
      await expect(other.getByLabel("Postman", { exact: true })).toBeChecked();
      expect(await other.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith("skill-day-profile:")))).toEqual([]);
      await page.getByLabel("Уровень", { exact: true }).selectOption("senior");
      await save(page);
      await other.getByLabel("Уровень", { exact: true }).selectOption("junior");
      await other.getByRole("button", { name: "Сохранить изменения", exact: true }).click();
      await expect(other.getByRole("alert")).toContainText("другом устройстве");
      await other.getByRole("button", { name: "Загрузить актуальный профиль", exact: true }).click();
      await expect(other.getByLabel("Уровень", { exact: true })).toHaveValue("senior");
      await page.reload();
      await expect(page.getByLabel("Уровень", { exact: true })).toHaveValue("senior");
    } finally {
      await device.close();
      await client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 });
    }
  });

  test("Only this user's legacy settings are imported, and stale local data never overwrites a saved server profile", async ({ page }) => {
    const { client, email } = await account();
    const id = JSON.parse(Buffer.from(client.token.split(".")[1], "base64url")).sub;
    const key = `skill-day-profile:${id}`;
    try {
      await page.goto("/");
      await page.evaluate(({ key }) => {
        localStorage.setItem(key, JSON.stringify({ direction: "analyst", level: "senior", regions: ["Москва"], skills: ["Работа с требованиями", "SQL"] }));
        localStorage.setItem("skill-day-profile:someone-else", JSON.stringify({ direction: "qa", level: "intern", regions: [], skills: [] }));
      }, { key });
      await login(page, email);
      await page.goto("/#/profile");
      await expect(page.getByLabel("Уровень", { exact: true })).toHaveValue("senior");
      await expect(page.getByLabel("Анализ требований", { exact: true })).toBeChecked();
      await expect(page.getByLabel("SQL", { exact: true })).toBeChecked();
      expect(await page.evaluate(key => localStorage.getItem(key), key)).toBeNull();
      await page.evaluate(key => localStorage.setItem(key, JSON.stringify({ direction: "qa", level: "intern", regions: [], skills: [] })), key);
      await page.reload();
      await expect(page.getByLabel("Уровень", { exact: true })).toHaveValue("senior");
      await expect(page.getByLabel("Направление", { exact: true })).toHaveValue("analyst");
    } finally { await client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 }); }
  });
});
