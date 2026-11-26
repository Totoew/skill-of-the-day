import test, { before, after, beforeEach } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { directions, basicSkills, getScope, coverage, layers, minimumDeficit, basisJobs, skillContext } from "../../../../frontend/src/data.js";

import { buildPlan } from "../../../../frontend/tests/helpers/legacy-calculator.js";

const project = fileURLToPath(new URL("../RecommendationService.Api/", import.meta.url));
const originalCatalog = JSON.parse(readFileSync(new URL("../../SkillService/catalog.example.json", import.meta.url), "utf8"));
const originalSnapshot = JSON.parse(readFileSync(new URL("../RecommendationService.Infrastructure/Fixtures/vacancies.demo.json", import.meta.url), "utf8"));
const userId = "e8abbc1f-b5b9-4edb-8fb3-febc3ab1303a";
let server, service, base, logs = "", profile, catalog, snapshot, failure, received;
const defaults = () => ({ userId, direction: "analyst", level: "all", regions: ["Москва", "Удалённая работа"], skills: ["Анализ требований", "Моделирование данных", "UML"], version: 7 });
async function listen(server) { await new Promise(resolve => server.listen(0, "127.0.0.1", resolve)); return server.address().port; }
before(async () => {
  server = createServer(async (request, response) => {
    let body = ""; for await (const chunk of request) body += chunk;
    const path = new URL(request.url, "http://localhost").pathname;
    received.push({ path, bearer: request.headers.authorization });
    response.setHeader("Content-Type", "application/json");
    if (failure?.path === path) { response.writeHead(failure.status || 503); return response.end(failure.body || '{}'); }
    if (path === "/auth/me") {
      if (request.headers.authorization !== "Bearer test-session-private") { response.writeHead(401); return response.end('{}'); }
      return response.end(JSON.stringify({ id: userId, roles: ["User"] }));
    }
    if (path === "/profile/me") return response.end(JSON.stringify(profile));
    if (path === "/skills/catalog") return response.end(JSON.stringify(catalog));
    if (path === "/vacancies/snapshots/latest") return response.end(JSON.stringify(snapshot));
    response.writeHead(404); response.end('{}');
  });
  received = [];
  const upstream = `http://127.0.0.1:${await listen(server)}/`;
  const reservation = createServer(); const port = await listen(reservation); await new Promise(resolve => reservation.close(resolve));
  base = `http://127.0.0.1:${port}`;
  service = spawn("dotnet", ["bin/Release/net10.0/RecommendationService.Api.dll", "--urls", base], { cwd: project,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", Auth__Address: upstream, Profile__Address: upstream, Skills__Address: upstream,
      Vacancies__Provider: "Http", Vacancies__Address: upstream }, stdio: ["ignore", "pipe", "pipe"] });
  service.stdout.on("data", chunk => { logs += chunk; }); service.stderr.on("data", chunk => { logs += chunk; });
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    if (service.exitCode !== null) throw new Error("Recommendation service failed: " + logs);
    try { if ((await fetch(base + "/health/live")).ok) return; } catch { /* Bind pending. */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("Service did not start: " + logs);
});
beforeEach(() => { profile = defaults(); catalog = { ...structuredClone(originalCatalog), version: 1 }; snapshot = structuredClone(originalSnapshot); failure = null; received = []; });
after(async () => {
  if (service?.exitCode === null) { const stopped = new Promise(resolve => service.once("exit", resolve)); service.kill("SIGTERM"); await stopped; }
  if (server?.listening) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});
async function plan(query = "", { expected = 200, method = "GET", body, token = "test-session-private", route = "/plan" } = {}) {
  const response = await fetch(base + "/recommendations" + route + query, { method,
    headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body ? { "Content-Type": "application/json" } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
  const json = await response.json(); assert.equal(response.status, expected, `${response.status}: ${json.title || ""}`); return json;
}

test("The C# calculator matches the previous JS results across all directions, levels, regions and deficit layers", async () => {
  for (const direction of Object.keys(directions)) for (const level of ["all", "junior", "middle", "senior", "intern"]) for (const regions of [["Москва", "Удалённая работа"], ["Удалённая работа"], ["Владивосток"]]) {
    profile = { ...defaults(), direction, level, regions, skills: [directions[direction].skills[0], directions[direction].skills[1], directions[direction].skills[4]] };
    const all = getScope(profile);
    const actual = await plan("?profileVersion=7");
    assert.equal(actual.source, "vacancy-service");
    assert.equal(actual.covered, coverage(all, profile.skills));
    assert.equal(actual.total, all.length);
    assert.deepEqual(actual.layers.map(item => [item.layer, item.count]), layers(all, profile.skills));
    assert.equal(actual.minimumDeficit, minimumDeficit(all, profile.skills));
    assert.deepEqual(actual.steps.map(step => [step.skills, step.gain]), buildPlan(all, profile.skills, direction).map(step => [step.skills, step.gain]));
    for (const value of actual.layers) {
      const result = await plan("?layer=" + value.layer);
      const jobs = all.filter(job => job.required.filter(skill => !profile.skills.includes(skill)).length === value.layer);
      assert.equal(result.total, value.count);
      assert.equal(result.covered, coverage(jobs, profile.skills));
      const reference = buildPlan(jobs, profile.skills, direction);
      assert.deepEqual(result.steps.map(step => [step.skills, step.gain]), reference.map(step => [step.skills, step.gain]));
      for (const step of result.steps) for (const metric of step.metrics) {
        assert.equal(metric.gain, metric.basisIds.length);
        assert.equal(metric.after - metric.before, metric.gain);
        assert.equal(metric.points, result.total ? 100 * metric.gain / result.total : 0);
        assert.deepEqual(metric.basisIds, basisJobs(jobs, skillContext(profile.skills, reference[step.index], metric.skill), metric.skill).map(job => job.id));
      }
    }
  }
});

test("New users see unfinished basics; empty slices have zero contribution and no layers", async () => {
  profile.skills = [];
  let result = await plan();
  assert.equal(result.noSkills, true); assert.equal(result.steps.length, 0); assert.equal(result.remainingBasics.length, 5);
  profile.regions = ["Владивосток"];
  result = await plan();
  assert.equal(result.total, 0); assert.deepEqual(result.layers, []); assert.equal(result.covered, 0);
  assert.ok(result.basics.every(metric => metric.gain === 0 && metric.points === 0 && metric.basisIds.length === 0));
});

test("Skill codes and aliases normalize to canonical names; unknown required skills, unrecognized and excluded jobs never inflate coverage", async () => {
  catalog.skills.find(skill => skill.code === "sql").synonyms = ["Structured Query Language"];
  snapshot.vacancies = [
    { ...snapshot.vacancies[0], id: "canonical", required: ["sql", "Structured Query Language"], url: "javascript:alert(1)" },
    { ...snapshot.vacancies[0], id: "unknown", required: ["Unknown required skill"] },
    { ...snapshot.vacancies[0], id: "unrecognized", required: [], structureRecognized: false },
    { ...snapshot.vacancies[0], id: "excluded", required: [], excluded: true },
  ];
  const result = await plan();
  assert.equal(result.total, 1); assert.equal(result.excludedVacancies, 3); assert.equal(result.covered, 0);
  assert.deepEqual(result.jobs[0].required, ["SQL"]); assert.equal(result.jobs[0].url, null);
  assert.equal(result.basics.find(metric => metric.skill === "SQL").gain, 1);
});

test("Joint steps expose each skill's matching evidence and support a partial five-skill bundle", async () => {
  const extra = Array.from({ length: 6 }, (_, index) => ({ code: "extra-" + index, name: "Extra " + index, synonyms: [], directions: ["analyst"], basicFor: [] }));
  catalog.skills.push(...extra);
  snapshot.vacancies = [{ ...snapshot.vacancies[0], required: extra.map(skill => skill.code) }];
  const result = await plan();
  assert.equal(result.steps[0].skills.length, 5); assert.equal(result.steps[0].partial, true); assert.equal(result.steps[0].gain, 0);
  assert.equal(result.steps[1].gain, 1);
  for (const step of result.steps) for (const metric of step.metrics) assert.equal(metric.gain, metric.basisIds.length);
});

test("Twenty skills form twenty ordered steps and catalogue basic flags are taken from the service", async () => {
  const extra = Array.from({ length: 20 }, (_, index) => ({ code: "extra-" + index, name: "Extra " + index, synonyms: [], directions: ["analyst"], basicFor: [] }));
  catalog.skills.push(...extra);
  snapshot.vacancies = extra.map((_, index) => ({ ...snapshot.vacancies[0], id: "stress-" + index, required: [...basicSkills.analyst, ...extra.slice(0, index + 1).map(skill => skill.code)] }));
  const result = await plan();
  assert.equal(result.steps.length, 20);
  assert.deepEqual(result.steps.flatMap(step => step.skills), extra.map(skill => skill.name));
  catalog.skills.find(skill => skill.code === "extra-0").basicFor = ["analyst"];
  assert.ok((await plan()).basicSkills.includes("Extra 0"));
});

test("The caller cannot supply another user's profile or vacancies; stale profile versions are rejected", async () => {
  for (const query of ["?userId=another", "?layer=-1", "?layer=257", "?layer=1&layer=2", "?profileVersion=-1"]) await plan(query, { expected: 400 });
  await plan("?profileVersion=6", { expected: 409 });
  await plan("", { route: "/recalculate", method: "POST", body: { skills: ["SQL"], userId: "other" }, expected: 400 });
  profile.userId = "4384eb08-68eb-43d5-a2ab-cb9c890a9381";
  await plan("", { expected: 503 });
});

test("Dependencies fail closed; invalid snapshots and expired sessions cannot become fake empty plans", async () => {
  for (const path of ["/auth/me", "/profile/me", "/skills/catalog", "/vacancies/snapshots/latest"]) {
    failure = { path, status: 503 }; await plan("", { expected: 503 });
  }
  failure = { path: "/profile/me", status: 401 }; await plan("", { expected: 401 });
  failure = { path: "/skills/catalog", status: 200, body: "{malformed" }; await plan("", { expected: 503 });
  failure = null; snapshot.vacancies.push(snapshot.vacancies[0]); await plan("", { expected: 503 });
  await plan("", { token: null, expected: 401 }); await plan("", { token: "revoked", expected: 401 });
});

test("Only the profile receives the user's Bearer; repeated calculation is deterministic and logs omit credentials", async () => {
  const one = await plan(), two = await plan("", { method: "POST", route: "/recalculate" });
  assert.deepEqual(two, one);
  assert.ok(received.filter(item => item.path === "/profile/me").every(item => item.bearer === "Bearer test-session-private"));
  assert.ok(received.filter(item => ["/skills/catalog", "/vacancies/snapshots/latest"].includes(item.path)).every(item => item.bearer === undefined));
  assert.ok(!logs.includes("test-session-private"));
  const doc = await fetch(base + "/recommendations/openapi/v1.json").then(response => response.json());
  assert.equal(doc.servers[0].url, "/api");
  assert.ok(doc.paths["/recommendations/plan"]); assert.ok(!doc.paths["/health/ready"]);
});
