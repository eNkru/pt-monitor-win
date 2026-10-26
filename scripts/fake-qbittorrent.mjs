#!/usr/bin/env node
// A stub qBittorrent WebUI, for verifying the client without a NAS.
//
// Why this exists: the real WebUI has behaviours that no network-free check
// can reproduce — most importantly that a private API call made *without a
// session* is answered with HTTP 403, the same status the login ban uses. A
// transport bug that dropped the `SID` cookie therefore looked exactly like
// "the server banned this IP". This stub can tell the difference.
//
// It mirrors qBittorrent 4.6.0 (and, with WEBAPI=5.2, 5.2) exactly where it
// matters:
//
//   authcontroller.cpp::loginAction
//       - banned IP          -> AccessDenied      => HTTP 403
//       - wrong credentials  -> "Fails."         => HTTP 200
//       - right credentials  -> "Ok." + Set-Cookie: SID
//   authcontroller.cpp::increaseFailedAttempts
//       - counts every failed login until a login SUCCEEDS (not time-limited)
//       - bans at MaxAuthenticationFailCount (default 5)
//   webapplication.cpp::doProcessRequest
//       - !session() && !isPublicAPI -> ForbiddenHTTPError => HTTP 403
//
// WEBAPI=5.2 switches to the 5.2 shapes, which moved the verdict from the
// body to the status code: banned -> 403, wrong credentials -> 401, success
// -> 204 with no body.
//
// Usage:
//   node scripts/fake-qbittorrent.mjs [port]     # default 18080
//   dotnet run --project src/PTMonitor.Cli -- --probe qbittorrent \
//       --host 127.0.0.1 --port 18080 --user admin --pass adminadmin
//
// Expected: a JSON snapshot and exit 0. Wrong credentials must report
// "auth failed" (not "banned") for the first 5 attempts, and only then
// "banned". Repeat with WEBAPI=5.2 — the outcome must be identical.

import http from "node:http";

const USER = "admin";
const PASS = "adminadmin";
const MAX_FAILS = 5;
const SID = "stubSIDvalue123";
const API52 = process.env.WEBAPI === "5.2";
const port = Number(process.argv[2] ?? 18080);

let failCount = 0;
let banned = false;

const server = http.createServer((request, response) => {
  const log = (message) => process.stderr.write(`stub: ${message}\n`);
  const respond = (code, body = "", headers = {}) => {
    response.writeHead(code, code === 204 ? headers : { ...headers, "Content-Length": Buffer.byteLength(body) });
    response.end(code === 204 ? undefined : body);
  };

  const url = new URL(request.url ?? "/", "http://stub");
  const hasSession = (request.headers.cookie ?? "").includes(`SID=${SID}`);

  if (request.method === "POST") {
    if (!url.pathname.startsWith("/api/v2/auth/login")) {
      respond(404, "not found");
      return;
    }
    let body = "";
    request.on("data", (chunk) => (body += chunk));
    request.on("end", () => {
      const params = Object.fromEntries(
        body.split("&").filter((pair) => pair.includes("=")).map((pair) => pair.split("=", 2).map(decodeURIComponent)),
      );
      const user = params.username ?? "";
      const password = params.password ?? "";

      if (banned) {
        log("login -> 403 BANNED (IP ban branch)");
        respond(403, "Your IP address has been banned after too many failed authentication attempts.");
        return;
      }

      if (user === USER && password === PASS) {
        failCount = 0;
        const cookie = { "Set-Cookie": `SID=${SID}; path=/; HttpOnly; SameSite=Strict` };
        if (API52) {
          log("login -> 204 (session started, no body)");
          respond(204, "", cookie);
        } else {
          log("login -> 200 Ok. (session started)");
          respond(200, "Ok.", cookie);
        }
        return;
      }

      failCount += 1;
      if (failCount >= MAX_FAILS) {
        banned = true;
        log(`login -> fail (attempt ${failCount}, NOW BANNED)`);
      } else {
        log(`login -> fail (attempt ${failCount}/${MAX_FAILS})`);
      }
      respond(API52 ? 401 : 200, API52 ? "Unauthorized" : "Fails.");
    });
    return;
  }

  // GET
  if (!hasSession) {
    log(`GET ${url.pathname} -> 403 (no session cookie)`);
    respond(403, "Forbidden");
    return;
  }
  if (url.pathname.startsWith("/api/v2/app/webapiVersion")) {
    respond(200, API52 ? "2.15.1" : "2.11.2");
  } else if (url.pathname.startsWith("/api/v2/app/version")) {
    respond(200, API52 ? "v5.2.0" : "v4.6.0");
  } else if (url.pathname.startsWith("/api/v2/sync/maindata")) {
    const a = "a".repeat(40);
    const b = "b".repeat(40);
    respond(200, JSON.stringify({
      rid: 1,
      full_update: true,
      torrents: {
        [a]: { hash: a, name: "t1", state: "downloading" },
        [b]: { hash: b, name: "t2", state: "stalledUP" },
      },
      server_state: {
        dl_info_speed: 1258291,
        up_info_speed: 348160,
        connection_status: "connected",
        free_space_on_disk: 442918502400,
      },
    }));
  } else {
    respond(404, "not found");
  }
});

server.listen(port, "127.0.0.1", () => {
  process.stderr.write(`stub: listening on 127.0.0.1:${port} (WEBAPI=${API52 ? "5.2" : "4.6"})\n`);
});
