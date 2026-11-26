import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { account, Client, base, password } from "../../../../auth-service/tests/helpers.mjs";

const project = fileURLToPath(new URL("../../../../", import.meta.url));
const settings = { direction: "analyst", level: "junior", regions: ["Москва", "Удалённая работа"], skills: ["SQL", "UML"], version: 0 };
async function profile(client, { method = "GET", body, expected = 200, path = "/me", token = client?.token } = {}) {
  const response = await fetch(`${base}/api/profile${path}`, {
    method, headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body ? { "Content-Type": "application/json" } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {})
  });
  const text = await response.text();
  let json; try { json = JSON.parse(text); } catch { json = null; }
  assert.equal(response.status, expected, `${method} profile${path}: ${response.status} ${json?.title || ""}`);
  return { json, headers: response.headers };
}
const remove = client => client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 });

test("Profiles require a live session and persist across independent logins without sharing another user's data", async () => {
  await profile(null, { expected: 401 });
  await profile(null, { expected: 401, token: "invalid" });
  const one = await account(), two = await account();
  try {
    const initial = (await profile(one.client)).json;
    assert.equal(initial.version, 0);
    assert.equal(initial.userId, JSON.parse(Buffer.from(one.client.token.split(".")[1], "base64url")).sub);
    const saved = await profile(one.client, { method: "PUT", body: settings });
    assert.equal(saved.json.version, 1);
    assert.match(saved.json.userId, /^[a-f0-9-]{36}$/);
    assert.ok(saved.json.updatedAt);
    assert.equal(saved.headers.get("cache-control"), "no-store");
    const device = new Client(); await device.login(one.email);
    assert.deepEqual((await profile(device)).json, saved.json);
    assert.equal((await profile(two.client)).json.version, 0);
    assert.notEqual((await profile(two.client)).json.userId, saved.json.userId);
    await profile(two.client, { method: "PUT", body: { ...settings, userId: saved.json.userId }, expected: 400 });
    await profile(two.client, { path: `/${saved.json.userId}`, expected: 404 });
    assert.deepEqual((await profile(one.client)).json, saved.json);
    const changed = await profile(device, { method: "PUT", body: { ...settings, level: "senior", version: 1, skills: ["C#", "SQL"] } });
    assert.equal(changed.json.version, 2);
    assert.equal((await profile(one.client)).json.level, "senior");
  } finally { await remove(one.client); await remove(two.client); }
});

test("Validation rejects unsupported values; deduplication and empty selections are consistent", async () => {
  const { client } = await account();
  try {
    for (const invalid of [ { direction: "other" }, { level: "expert" }, { regions: ["unknown"] }, { skills: ["made up"] }, { skills: null }, { regions: null }, { version: -1 } ])
      await profile(client, { method: "PUT", body: { ...settings, ...invalid }, expected: 400 });
    assert.equal((await profile(client)).json.version, 0);
    const saved = (await profile(client, { method: "PUT", body: { ...settings, regions: [], skills: ["SQL", "SQL"] } })).json;
    assert.deepEqual(saved.regions, []);
    assert.deepEqual(saved.skills, ["SQL"]);
    const cleared = (await profile(client, { method: "PUT", body: { ...settings, version: 1, regions: [], skills: [] } })).json;
    assert.deepEqual(cleared.skills, []);
  } finally { await remove(client); }
});

test("Concurrent writes conflict instead of silently overwriting settings; reset allows a fresh profile", async () => {
  const { client } = await account();
  try {
    await profile(client, { method: "PUT", body: settings });
    const responses = await Promise.all(["middle", "senior"].map(level => fetch(`${base}/api/profile/me`, {
      method: "PUT", headers: { Authorization: `Bearer ${client.token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ ...settings, level, version: 1 })
    })));
    assert.deepEqual(responses.map(response => response.status).sort(), [200, 409]);
    const winner = await responses.find(response => response.status === 200).json();
    assert.deepEqual((await profile(client)).json, winner);
    await profile(client, { method: "DELETE", expected: 204 });
    assert.equal((await profile(client)).json.version, 0);
    assert.equal((await profile(client, { method: "PUT", body: settings })).json.version, 1);
  } finally { await remove(client); }
});

test("Logout revokes an already-issued access token for ProfileService immediately", async () => {
  const { client, email } = await account();
  try {
    await profile(client);
    await client.call("/logout", undefined, { expected: 204 });
    await profile(client, { expected: 401 });
    await client.login(email);
    await profile(client);
  } finally { await remove(client); }
});

test("Profile Swagger works through Gateway and internal cleanup is not a public route", async () => {
  const document = await fetch(`${base}/api/profile/openapi/v1.json`).then(response => response.json());
  assert.equal(document.servers[0].url, "/api");
  assert.ok(document.paths["/profile/me"].put);
  assert.ok(document.components.securitySchemes.bearer);
  assert.ok(!Object.keys(document.paths).some(path => path.includes("internal")));
  const ui = await fetch(`${base}/api/profile/swagger/index.html`);
  assert.equal(ui.status, 200);
  assert.match(await ui.text(), /Swagger UI/);
  assert.equal((await fetch(`${base}/api/profile/swagger/swagger-ui-bundle.js`)).status, 200);
  assert.equal((await fetch(`${base}/api/internal/profiles/00000000-0000-0000-0000-000000000000`, { method: "DELETE" })).status, 404);
});

test("Deleting the account durably queues cleanup and removes its stored profile", async () => {
  const { client } = await account();
  const saved = (await profile(client, { method: "PUT", body: settings })).json;
  await remove(client);
  await profile(client, { expected: 401 });
  // UUID is validated before being used in the local test query. No passwords or bearer values are printed.
  assert.match(saved.userId, /^[a-f0-9-]{36}$/);
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    const count = execFileSync("docker", ["compose", "exec", "-T", "auth-db", "psql", "-U", "auth", "-d", "skill_day_auth", "-tAc",
      `SELECT (SELECT count(*) FROM profile.user_profiles WHERE user_id='${saved.userId}'), (SELECT count(*) FROM auth."AccountDeletions" WHERE "UserId"='${saved.userId}' AND "CompletedAt" IS NOT NULL)`], { cwd: project, encoding: "utf8" }).trim();
    if (count === "0|1") return;
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  assert.fail("Account deletion did not clean up the profile within 20 seconds");
});

test("Aspire correlates a profile request across Gateway, ProfileService and AuthService", async () => {
  const { client } = await account();
  try {
    const result = await profile(client, { method: "PUT", body: settings });
    const id = result.headers.get("x-trace-id");
    assert.match(id, /^[a-f0-9]{32}$/);
    assert.equal(result.headers.get("x-request-id"), id);
    const dashboard = process.env.ASPIRE_TEST_ORIGIN || "http://127.0.0.1:18888";
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      const response = await fetch(`${dashboard}/api/telemetry/traces/${id}`);
      if (response.ok) {
        const trace = await response.json();
        const resources = trace.data.resourceSpans.map(resource => resource.resource.attributes.find(attribute => attribute.key === "service.name")?.value?.stringValue);
        if (["api-gateway", "profile-service", "auth-service"].every(name => resources.includes(name))) {
          assert.ok(!JSON.stringify(trace).includes(client.token));
          console.log(`Profile trace: ${dashboard}/traces/detail/${id}`);
          return;
        }
      }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.fail("No three-service profile trace in Aspire");
  } finally { await remove(client); }
});
