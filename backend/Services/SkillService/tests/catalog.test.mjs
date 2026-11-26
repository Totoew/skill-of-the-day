import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { administrator, skills, remove, sql } from "./helpers.mjs";
import { account, base } from "../../../../auth-service/tests/helpers.mjs";

const prefix = "import-test-" + randomBytes(5).toString("hex");
const definition = (suffix, extras = {}) => ({ code: `${prefix}-${suffix}`, name: `${prefix} ${suffix}`, synonyms: [], directions: ["analyst"], basicFor: [], ...extras });
const document = (...values) => ({ schemaVersion: 1, skills: values });
let admin;
before(async () => { admin = await administrator(); });
after(async () => {
  if (!admin) return;
  // Only unique test-owned rows are removed; the user's catalogue is never restored from a stale snapshot.
  sql(`BEGIN; SELECT pg_advisory_xact_lock(72619112501); DELETE FROM skill.skills WHERE code LIKE '${prefix}-%'; UPDATE skill.catalog_state SET version=version+1 WHERE id=1; DELETE FROM skill.catalog_imports WHERE actor_id='${admin.id}'; COMMIT;`);
  await remove(admin.client);
});

test("Only a live administrator session can preview, import, export and download a template", async () => {
  const regular = await account();
  try {
    for (const path of ["/admin/template", "/admin/export", "/admin/import/preview", "/admin/import"]) {
      const body = path.includes("/import") ? document(definition("denied")) : undefined;
      await skills(null, path, body, 401);
      await skills(regular.client, path, body, 403);
    }
    assert.equal((await skills(null, "/catalog")).json.schemaVersion, 1);
    assert.equal((await skills(admin.client, "/admin/template")).json.schemaVersion, 1);
  } finally { await remove(regular.client); }
});

test("Preview is read-only; import persists new entries, aliases and basic flags, supports idempotent reimport and export", async () => {
  const item = definition("a", { synonyms: [`${prefix} alias`], basicFor: ["analyst"] });
  const original = (await skills(null, "/catalog")).json;
  const preview = (await skills(admin.client, "/admin/import/preview", document(item))).json;
  assert.equal(preview.added, 1);
  assert.deepEqual((await skills(null, "/catalog")).json, original);
  const saved = (await skills(admin.client, "/admin/import", { catalog: document(item), version: preview.version })).json;
  assert.equal(saved.version, original.version + 1);
  assert.equal(saved.added, 1);
  const catalog = (await skills(null, "/catalog")).json;
  assert.deepEqual(catalog.skills.find(skill => skill.code === item.code), item);
  assert.equal(catalog.skills.length, original.skills.length + 1);
  const repeat = (await skills(admin.client, "/admin/import", { catalog: document(item), version: saved.version })).json;
  assert.equal(repeat.unchanged, 1); assert.equal(repeat.version, saved.version);
  const exported = (await skills(admin.client, "/admin/export")).json;
  assert.equal(exported.version, undefined);
  assert.equal(exported.skills.length, catalog.skills.length);
  assert.ok(Number(sql(`SELECT count(*) FROM skill.catalog_imports WHERE actor_id='${admin.id}' AND added=1;`)) > 0);
});

test("Malformed values, unsupported fields and duplicates reject the whole file without changing the catalogue", async () => {
  const before = (await skills(null, "/catalog")).json;
  const valid = definition("valid");
  for (const invalid of [
    { schemaVersion: 2, skills: [valid] }, { schemaVersion: 1, skills: [] }, { schemaVersion: 1, skills: null },
    document(null), document(valid, valid), document(definition("bad", { name: " " })),
    document(definition("bad", { synonyms: [null] })), document(definition("bad", { directions: ["unknown"] })),
    document(definition("bad", { basicFor: ["qa"] })), document(definition("bad", { code: "BAD code" })),
    document(definition("one", { synonyms: [" duplicate "] })),
    document(definition("one"), definition("two", { synonyms: [definition("one").name.toUpperCase()] })),
    { ...document(valid), regions: ["Москва"] }, document({ ...valid, unknown: true }),
  ]) {
    await skills(admin.client, "/admin/import/preview", invalid, 400);
    await skills(admin.client, "/admin/import", { catalog: invalid, version: before.version }, 400);
  }
  assert.deepEqual((await skills(null, "/catalog")).json, before);
  const malformed = await fetch(`${base}/api/skills/admin/import/preview`, { method: "POST", headers: { Authorization: `Bearer ${admin.client.token}`, "Content-Type": "application/json" }, body: "{broken" });
  assert.equal(malformed.status, 400);
});

test("Aliases collide with existing canonical names; renaming cannot orphan saved profile skills", async () => {
  const before = (await skills(null, "/catalog")).json;
  const existing = before.skills[0];
  for (const invalid of [document(definition("collision", { synonyms: [existing.name.toLowerCase()] })), document({ ...existing, name: existing.name + " renamed" })])
    await skills(admin.client, "/admin/import/preview", invalid, 409);
  assert.deepEqual((await skills(null, "/catalog")).json, before);
});

test("Updates replace fields for included skills, allow atomic alias transfers and retain omitted records", async () => {
  const old = (await skills(null, "/catalog")).json.skills.find(item => item.code === `${prefix}-a`);
  const other = definition("b", { synonyms: old.synonyms });
  const changed = { ...old, synonyms: [], directions: ["analyst", "qa"], basicFor: ["qa"] };
  const value = document(other, changed);
  const preview = (await skills(admin.client, "/admin/import/preview", value)).json;
  assert.equal(preview.added, 1); assert.equal(preview.updated, 1);
  await skills(admin.client, "/admin/import", { catalog: value, version: preview.version });
  const actual = (await skills(null, "/catalog")).json;
  assert.deepEqual(actual.skills.find(item => item.code === old.code), changed);
  assert.deepEqual(actual.skills.find(item => item.code === other.code), other);
});

test("Concurrent imports require a fresh preview instead of overwriting a newer catalogue", async () => {
  const one = document(definition("c")), two = document(definition("d"));
  const preview = (await skills(admin.client, "/admin/import/preview", one)).json;
  const results = await Promise.all([one, two].map(catalog => fetch(`${base}/api/skills/admin/import`, {
    method: "POST", headers: { Authorization: `Bearer ${admin.client.token}`, "Content-Type": "application/json" }, body: JSON.stringify({ catalog, version: preview.version })
  })));
  assert.deepEqual(results.map(item => item.status).sort(), [200, 409]);
  const actual = (await skills(null, "/catalog")).json;
  assert.equal(actual.skills.filter(item => item.code === one.skills[0].code || item.code === two.skills[0].code).length, 1);
});

test("Imported skills are accepted by ProfileService through the Gateway", async () => {
  const user = await account();
  try {
    const response = await fetch(`${base}/api/profile/me`, { method: "PUT", headers: { Authorization: `Bearer ${user.client.token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ direction: "analyst", level: "all", regions: [], skills: [definition("a").name], version: 0 }) });
    assert.equal(response.status, 200);
    assert.deepEqual((await response.json()).skills, [definition("a").name]);
  } finally { await remove(user.client); }
});

test("Swagger and correlation headers are available through the Gateway", async () => {
  const result = await skills(null, "/openapi/v1.json");
  assert.equal(result.json.servers[0].url, "/api");
  assert.ok(result.json.paths["/skills/admin/import"].post.security.length);
  assert.ok(!result.json.paths["/skills/catalog"].get.security?.length);
  assert.match(result.headers.get("x-trace-id"), /^[a-f0-9]{32}$/);
  assert.equal((await fetch(`${base}/api/skills/swagger/index.html`)).status, 200);
});
