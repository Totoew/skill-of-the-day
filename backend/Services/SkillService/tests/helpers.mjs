import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { account, password, base } from "../../../../auth-service/tests/helpers.mjs";

export const project = fileURLToPath(new URL("../../../../", import.meta.url));
export function sql(statement) {
  return execFileSync("docker", ["compose", "exec", "-T", "auth-db", "psql", "-v", "ON_ERROR_STOP=1", "-U", "auth", "-d", "skill_day_auth", "-tA"],
    { cwd: project, input: statement, encoding: "utf8" }).trim();
}
export async function administrator() {
  const result = await account();
  const id = JSON.parse(Buffer.from(result.client.token.split(".")[1], "base64url")).sub;
  assert.match(id, /^[a-f0-9-]{36}$/);
  // Only the disposable account created by this test receives Admin; existing users are never changed.
  sql(`INSERT INTO auth."AspNetUserRoles" ("UserId", "RoleId") SELECT '${id}', "Id" FROM auth."AspNetRoles" WHERE "Name"='Admin' ON CONFLICT DO NOTHING;`);
  await result.client.login(result.email);
  return { ...result, id };
}
export const remove = client => client.call("/account", { currentPassword: password }, { method: "DELETE", auth: true, expected: 204 });
export async function skills(client, path, body, expected = 200, method = body === undefined ? "GET" : "POST") {
  const response = await fetch(`${base}/api/skills${path}`, { method,
    headers: { ...(client?.token ? { Authorization: `Bearer ${client.token}` } : {}), ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  const text = await response.text(); let json; try { json = JSON.parse(text); } catch { json = null; }
  assert.equal(response.status, expected, `${method} skills${path}: ${response.status} ${json?.title || ""}`);
  return { json, headers: response.headers };
}
