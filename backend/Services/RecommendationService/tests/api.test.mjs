import test from "node:test";
import assert from "node:assert/strict";
import { account, Client, base, password } from "../../../../auth-service/tests/helpers.mjs";

const partial = { direction: "analyst", level: "all", regions: ["Москва", "Удалённая работа"], skills: ["Анализ требований", "Моделирование данных", "UML"], version: 0 };
const remove = client => client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 });
async function save(client, settings) {
  const response = await fetch(`${base}/api/profile/me`, { method: "PUT", headers: { Authorization: `Bearer ${client.token}`, "Content-Type": "application/json" }, body: JSON.stringify(settings) });
  assert.equal(response.status, 200); return response.json();
}
async function plan(client, query = "", { expected = 200, recalculate = false, token = client?.token } = {}) {
  const response = await fetch(`${base}/api/recommendations/${recalculate ? "recalculate" : "plan"}${query}`, {
    method: recalculate ? "POST" : "GET", headers: token ? { Authorization: `Bearer ${token}` } : {} });
  const json = await response.json(); assert.equal(response.status, expected, `${response.status} ${json.title || ""}`);
  return { json, headers: response.headers };
}

test("The real profile and catalogue produce a server-side plan with matching contributions and evidence", async () => {
  const { client } = await account();
  try {
    const profile = await save(client, partial);
    const { json: result } = await plan(client, `?profileVersion=${profile.version}`);
    assert.equal(result.source, "demo"); assert.equal(result.updateIntervalDays, 5); assert.equal(result.profileVersion, profile.version);
    assert.equal(result.covered, 24); assert.equal(result.total, 120); assert.equal(result.noSkills, false);
    assert.deepEqual(result.steps.map(step => [step.skills, step.gain]), [[["Git"], 24], [["BPMN"], 16], [["SOAP"], 8]]);
    assert.ok(result.catalogVersion > 0);
    for (const step of result.steps) for (const metric of step.metrics) {
      assert.equal(metric.gain, metric.basisIds.length); assert.equal(metric.after - metric.before, metric.gain);
      assert.ok(metric.basisIds.every(id => result.jobs.some(job => job.id === id)));
    }
    assert.deepEqual((await plan(client, `?profileVersion=${profile.version}`, { recalculate: true })).json, result);
    const selected = (await plan(client, "?layer=3")).json;
    assert.equal(selected.total, 46); assert.equal(selected.covered, 0);
    assert.deepEqual(selected.steps.map(step => step.gain), [24, 14, 8]);
  } finally { await remove(client); }
});

test("Independent accounts receive their own plans; stale profiles are rejected and empty slices stay zero", async () => {
  const one = await account(), two = await account();
  try {
    const profile = await save(one.client, partial);
    await save(two.client, { ...partial, skills: [...partial.skills, "SQL", "REST API", "Git"] });
    assert.equal((await plan(one.client)).json.covered, 24);
    assert.equal((await plan(two.client)).json.covered, 96);
    await plan(one.client, `?userId=${JSON.parse(Buffer.from(two.client.token.split(".")[1], "base64url")).sub}`, { expected: 400 });
    await save(one.client, { ...partial, version: profile.version, regions: ["Владивосток"] });
    await plan(one.client, `?profileVersion=${profile.version}`, { expected: 409 });
    const result = (await plan(one.client)).json;
    assert.equal(result.total, 0); assert.equal(result.covered, 0); assert.deepEqual(result.layers, []); assert.deepEqual(result.steps, []);
    assert.ok(result.basics.every(metric => metric.gain === 0 && metric.points === 0));
  } finally { await remove(one.client); await remove(two.client); }
});

test("Calculation requires a live session and logout revokes access immediately", async () => {
  await plan(null, "", { expected: 401 }); await plan(null, "", { token: "invalid", expected: 401 });
  const { client, email } = await account();
  try {
    const initial = (await plan(client)).json;
    assert.equal(initial.noSkills, true); assert.equal(initial.steps.length, 0); assert.equal(initial.remainingBasics.length, 5);
    await client.call("/logout", undefined, { expected: 204 });
    await plan(client, "", { expected: 401 });
    await client.login(email);
    await plan(client);
  } finally { await remove(client); }
});

test("Swagger is exposed via Gateway; Aspire correlates the calculation across five services without logging credentials", async () => {
  const { client } = await account();
  try {
    const response = await fetch(`${base}/api/recommendations/openapi/v1.json`); assert.equal(response.status, 200);
    const doc = await response.json(); assert.equal(doc.servers[0].url, "/api"); assert.ok(doc.paths["/recommendations/recalculate"]);
    assert.equal((await fetch(`${base}/api/recommendations/swagger/index.html`)).status, 200);
    const { headers } = await plan(client);
    const traceId = headers.get("x-trace-id"); assert.match(traceId, /^[a-f0-9]{32}$/); assert.equal(headers.get("x-request-id"), traceId);
    assert.equal(headers.get("cache-control"), "no-store");
    const origin = process.env.ASPIRE_TEST_ORIGIN || "http://127.0.0.1:18888", deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      const trace = await fetch(`${origin}/api/telemetry/traces/${traceId}`).then(response => response.json());
      const resources = trace.data?.resourceSpans || [];
      const names = new Set(resources.flatMap(item => (item.resource?.attributes || []).filter(attribute => attribute.key === "service.name").map(attribute => attribute.value?.stringValue)));
      if (["api-gateway", "recommendation-service", "profile-service", "skill-service", "auth-service"].every(name => names.has(name))) {
        assert.ok(!JSON.stringify(trace).includes(client.token));
        console.log(`Recommendation trace: ${origin}/traces/detail/${traceId}`); return;
      }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.fail("Aspire did not receive a trace across the five services");
  } finally { await remove(client); }
});
