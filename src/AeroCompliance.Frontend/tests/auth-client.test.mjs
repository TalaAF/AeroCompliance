import assert from "node:assert/strict";
import { readFile, writeFile, unlink } from "node:fs/promises";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";
import test, { after } from "node:test";
import axios, { AxiosError } from "axios";
import { transform } from "esbuild";

const root = path.dirname(fileURLToPath(import.meta.url));
const source = path.resolve(root, "../src/api/client.ts");
const compiled = path.resolve(
  root,
  `../src/api/.auth-client-test-${process.pid}.mjs`,
);
const { code } = await transform(await readFile(source, "utf8"), {
  loader: "ts",
  format: "esm",
});
await writeFile(compiled, code);

const savedAdapter = axios.defaults.adapter;
const values = new Map();
const events = [];
globalThis.sessionStorage = {
  getItem: (key) => values.get(key) ?? null,
  setItem: (key, value) => values.set(key, value),
  removeItem: (key) => values.delete(key),
};
globalThis.window = { dispatchEvent: (event) => events.push(event.type) };
let mode = "";

function failure(config, status) {
  const error = new AxiosError("request failed", "ERR_BAD_RESPONSE", config);
  error.response = {
    status,
    data: { title: "request failed" },
    config,
    headers: {},
    statusText: "",
  };
  throw error;
}

axios.defaults.adapter = async (config) => {
  if (config.url === "/api/auth/refresh") {
    if (mode === "refresh-unavailable") failure(config, 503);
    if (mode === "refresh-rejected") failure(config, 401);
    return {
      data: {
        accessToken: "new-access",
        refreshToken: "new-refresh",
        accessTokenExpiresAt: new Date().toISOString(),
      },
      status: 200,
      statusText: "OK",
      headers: {},
      config,
    };
  }
  if (config.url === "/resource") {
    if (config.headers.Authorization === "Bearer new-access")
      failure(config, 403);
    failure(config, 401);
  }
  throw new Error(`Unexpected URL ${config.url}`);
};

const client = await import(pathToFileURL(compiled).href);
const initial = {
  accessToken: "old-access",
  refreshToken: "old-refresh",
  accessTokenExpiresAt: new Date().toISOString(),
};

test("temporary refresh failure retains the browser session", async () => {
  values.clear();
  events.length = 0;
  mode = "refresh-unavailable";
  client.storeTokens(initial);
  await assert.rejects(
    client.api.get("/resource"),
    (error) => error.status === 503,
  );
  assert.equal(client.getRefreshToken(), "old-refresh");
  assert.deepEqual(events, []);
});

test("a failed retried API request does not discard newly rotated tokens", async () => {
  values.clear();
  events.length = 0;
  mode = "retry-forbidden";
  client.storeTokens(initial);
  await assert.rejects(
    client.api.get("/resource"),
    (error) => error.status === 403,
  );
  assert.equal(client.getRefreshToken(), "new-refresh");
  assert.deepEqual(events, []);
});

test("a rejected refresh clears tokens and announces expiry", async () => {
  values.clear();
  events.length = 0;
  mode = "refresh-rejected";
  client.storeTokens(initial);
  await assert.rejects(
    client.api.get("/resource"),
    (error) => error.status === 401,
  );
  assert.equal(client.getRefreshToken(), null);
  assert.deepEqual(events, ["aerocompliance:expired"]);
});

after(async () => {
  axios.defaults.adapter = savedAdapter;
  await unlink(compiled);
});
