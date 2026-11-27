// =============================================================================
// Acme Insurance: web front end (Node.js 24)
//
// Serves the static site and forwards /api/* to quote-api. It uses only what
// ships with Node.js: no npm packages, so nothing but the runtime and this
// file can carry a vulnerability, and there is no install step that needs a
// shell or a package manager in the image.
// =============================================================================
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PORT = Number(process.env.PORT || 8080);
const API_URL = (process.env.API_URL || "http://localhost:8081").replace(/\/+$/, "");
const PUBLIC_DIR = resolve(fileURLToPath(new URL("./public", import.meta.url)));
const MAX_BODY = 64 * 1024;
const API_TIMEOUT_MS = 8000;

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".svg": "image/svg+xml",
  ".json": "application/json; charset=utf-8",
  ".ico": "image/x-icon",
};

const SECURITY_HEADERS = {
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
  "Content-Security-Policy": "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:",
};

const log = (msg) => process.stdout.write(`${new Date().toISOString()} ${msg}\n`);

function send(res, status, body, headers = {}) {
  res.writeHead(status, { ...SECURITY_HEADERS, ...headers });
  res.end(body);
}

function sendJson(res, status, value) {
  send(res, status, JSON.stringify(value), { "Content-Type": TYPES[".json"], "Cache-Control": "no-store" });
}

function selfInfo() {
  return {
    service: "web",
    runtime: `Node.js ${process.version}`,
    os: `${process.platform} ${process.arch}`,
    baseImage: process.env.BASE_IMAGE || "unknown",
    baseCreated: process.env.BASE_CREATED || "unknown",
    hasShell: existsSync("/bin/sh") || existsSync("/usr/bin/sh"),
    hasPackageManager: ["/usr/bin/dnf", "/usr/bin/microdnf", "/usr/bin/rpm", "/usr/bin/npm", "/usr/local/bin/npm"].some(existsSync),
    runsAsRoot: typeof process.getuid === "function" ? process.getuid() === 0 : false,
  };
}

async function readBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY) throw Object.assign(new Error("Request body too large"), { status: 413 });
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

async function callApi(path, { method = "GET", body } = {}) {
  return fetch(`${API_URL}${path}`, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body,
    signal: AbortSignal.timeout(API_TIMEOUT_MS),
  });
}

async function proxy(req, res, path) {
  if (!["GET", "POST"].includes(req.method)) return sendJson(res, 405, { error: "Method not allowed." });
  try {
    const body = req.method === "POST" ? await readBody(req) : undefined;
    const upstream = await callApi(path, { method: req.method, body });
    const text = await upstream.text();
    send(res, upstream.status, text, {
      "Content-Type": upstream.headers.get("content-type") || TYPES[".json"],
      "Cache-Control": "no-store",
    });
  } catch (err) {
    if (err.status === 413) return sendJson(res, 413, { error: err.message });
    log(`proxy error ${req.method} ${path}: ${err.message}`);
    sendJson(res, 502, { error: "The quote service is not reachable right now. Try again in a moment." });
  }
}

async function info(res) {
  let api = { service: "quote-api", error: "not reachable" };
  try {
    const upstream = await callApi("/api/info");
    if (upstream.ok) api = await upstream.json();
  } catch { /* keep the fallback */ }
  sendJson(res, 200, { web: selfInfo(), api });
}

async function serveStatic(res, pathname) {
  const relative = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
  const file = normalize(join(PUBLIC_DIR, relative));
  if (file !== PUBLIC_DIR && !file.startsWith(PUBLIC_DIR + "/")) return send(res, 403, "Forbidden");
  try {
    const data = await readFile(file);
    send(res, 200, data, {
      "Content-Type": TYPES[extname(file)] || "application/octet-stream",
      "Cache-Control": "no-cache",
    });
  } catch {
    send(res, 404, "Not found", { "Content-Type": "text/plain; charset=utf-8" });
  }
}

const server = createServer(async (req, res) => {
  let pathname;
  let search;
  try {
    ({ pathname, search } = new URL(req.url, "http://localhost"));
    pathname = decodeURIComponent(pathname);
  } catch {
    return send(res, 400, "Bad request");
  }

  if (pathname === "/healthz") return sendJson(res, 200, { status: "ok" });
  if (pathname === "/api/info") return info(res);
  if (pathname.startsWith("/api/")) return proxy(req, res, pathname + search);
  if (req.method !== "GET" && req.method !== "HEAD") return send(res, 405, "Method not allowed");
  return serveStatic(res, pathname);
});

server.listen(PORT, "0.0.0.0", () => log(`web listening on :${PORT}, quote-api at ${API_URL}`));

// The container has no shell and no init to forward signals, so handle them here.
for (const signal of ["SIGTERM", "SIGINT"]) {
  process.on(signal, () => {
    log(`${signal} received, shutting down`);
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 5000).unref();
  });
}
