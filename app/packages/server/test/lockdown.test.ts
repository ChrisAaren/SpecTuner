import { describe, expect, test } from "bun:test"
import { Effect } from "effect"
import { HttpApi } from "effect/http-api"
import { tmpdirScoped } from "../../core/test/fixture/tmpdir"
import { it } from "../../core/test/lib/effect"
import { Api } from "../src/api"
import { ServerFetch } from "../src/fetch"
import { ServerLockdown } from "../src/middleware/lockdown"

const PASSWORD = "secret"
const AUTH = { authorization: `Basic ${btoa(`spectuner:${PASSWORD}`)}` }
const FILE_READ = "/api/fs/read/*"

/**
 * Every route SpecTuner's lock-down leaves reachable. A new route fails the coverage test below until it is listed
 * here or refused in `ServerLockdown.RULES`, so nobody adds an endpoint without deciding.
 */
const ALLOWED = new Set([
  "GET /api/info",
  "GET /api/location",
  "POST /api/location/reload",
  "GET /api/agent",
  "GET /api/agent/:agentID",
  "GET /api/plugin",
  "GET /api/plugin/gui",
  "GET /api/session",
  "POST /api/session",
  "GET /api/session/active",
  "GET /api/session/:sessionID",
  "DELETE /api/session/:sessionID",
  "PATCH /api/session/:sessionID",
  "POST /api/session/:sessionID/fork",
  "POST /api/session/:sessionID/agent",
  "POST /api/session/:sessionID/model",
  "POST /api/session/:sessionID/move",
  "POST /api/session/:sessionID/prompt",
  "POST /api/session/:sessionID/synthetic",
  "POST /api/session/:sessionID/compact",
  "POST /api/session/:sessionID/revert/stage",
  "DELETE /api/session/:sessionID/revert",
  "POST /api/session/:sessionID/revert/commit",
  "GET /api/session/:sessionID/context",
  "GET /api/session/:sessionID/diff",
  "GET /api/session/:sessionID/inbox",
  "DELETE /api/session/:sessionID/inbox/:inboxID",
  "PATCH /api/session/:sessionID/inbox/:inboxID",
  "POST /api/session/:sessionID/generate",
  "POST /api/session/:sessionID/interrupt",
  "POST /api/session/:sessionID/background",
  "GET /api/session/:sessionID/message",
  "GET /api/session/:sessionID/message/:messageID",
  "GET /api/session/:sessionID/form",
  "POST /api/session/:sessionID/form",
  "GET /api/session/:sessionID/form/:formID",
  "POST /api/session/:sessionID/form/:formID/reply",
  "DELETE /api/session/:sessionID/form/:formID",
  "POST /api/session/:sessionID/view",
  "GET /api/session/:sessionID/permission",
  "POST /api/session/:sessionID/permission",
  "GET /api/session/:sessionID/permission/:requestID",
  "POST /api/session/:sessionID/permission/:requestID/reply",
  "GET /api/experimental/session/stats",
  "POST /api/experimental/session/import",
  "GET /api/experimental/session/:sessionID/export",
  "POST /api/experimental/session/:sessionID/wait",
  "GET /api/experimental/session/:sessionID/log",
  "GET /api/experimental/session/:sessionID/instructions/entries",
  "PUT /api/experimental/session/:sessionID/instructions/entries/:key",
  "DELETE /api/experimental/session/:sessionID/instructions/entries/:key",
  "GET /api/model",
  "GET /api/model/default",
  "GET /api/provider",
  "GET /api/provider/:providerID",
  "GET /api/integration",
  "GET /api/integration/:integrationID",
  "POST /api/integration/:integrationID/connect/key",
  "POST /api/integration/:integrationID/connect/external",
  "POST /api/integration/:integrationID/connect/oauth",
  "GET /api/integration/:integrationID/connect/oauth/:attemptID",
  "POST /api/integration/:integrationID/connect/oauth/:attemptID/complete",
  "DELETE /api/integration/:integrationID/connect/oauth/:attemptID",
  "GET /api/credential",
  "POST /api/credential",
  "PATCH /api/credential/:credentialID",
  "POST /api/credential/:credentialID/activate",
  "DELETE /api/credential/:credentialID",
  "GET /api/project",
  "PATCH /api/project/:projectID",
  "GET /api/form",
  "GET /api/permission/request",
  "GET /api/permission/saved",
  "PATCH /api/permission/saved/:id",
  "DELETE /api/permission/saved/:id",
  "GET /api/fs/list",
  "GET /api/fs/find",
  "GET /api/command",
  "GET /api/skill",
  "POST /api/rpc/:rpcID/:method",
  "GET /api/event",
  "GET /api/reference",
  "GET /api/worktree",
  "POST /api/worktree/refresh",
  "GET /api/vcs",
  "GET /api/vcs/base",
  "GET /api/vcs/status",
  "GET /api/vcs/branch",
  "GET /api/vcs/diff",
  "GET /api/debug/location",
  "DELETE /api/debug/location",
  "GET /api/mcp",
  "GET /api/mcp/resource",
  "GET /api/config",
  "GET /api/config/shell",
  // The app's location sync lists running shells; none can be started, so these only ever see an empty list.
  "GET /api/shell",
  "GET /api/shell/:id",
  "GET /api/shell/:id/output",
  "DELETE /api/shell/:id",
  "GET /api/experimental/usage/limits",
  "GET /api/experimental/migration/v1",
  "GET /api/experimental/media/:hash",
  "GET /api/experimental/blob/:id",
  "GET /api/experimental/job",
  "POST /api/experimental/job/:id/cancel",
])

function endpoints() {
  const found: { method: string; path: string }[] = []
  HttpApi.reflect(Api, {
    onGroup() {},
    onEndpoint({ endpoint }) {
      found.push({ method: endpoint.method, path: endpoint.path })
    },
  })
  return found
}

/** A concrete URL for a route template: parameters become `x`, the wildcard a file name. */
function concrete(path: string, file = "notes.txt") {
  return path.replace(/:[A-Za-z]+/g, "x").replace(/\*$/, file)
}

const withLockdown = <A, E, R>(value: string | undefined, body: Effect.Effect<A, E, R>) =>
  Effect.acquireUseRelease(
    Effect.sync(() => {
      const previous = process.env[ServerLockdown.ENV]
      if (value === undefined) delete process.env[ServerLockdown.ENV]
      else process.env[ServerLockdown.ENV] = value
      return previous
    }),
    () => body,
    (previous) =>
      Effect.sync(() => {
        if (previous === undefined) delete process.env[ServerLockdown.ENV]
        else process.env[ServerLockdown.ENV] = previous
      }),
  )

const serve = (lockdown: string | undefined, auth: "password" | "none" = "password") =>
  withLockdown(
    lockdown,
    ServerFetch.make({
      app: { version: "test" },
      database: { path: ":memory:" },
      fs: { filewatcher: false },
      models: { fetch: false },
      password: auth === "password" ? PASSWORD : undefined,
    }),
  )

function send(
  handler: (request: Request) => Promise<Response>,
  method: string,
  path: string,
  directory: string,
  headers: Record<string, string> = AUTH,
) {
  const url = new URL(path, "http://spectuner.local")
  url.searchParams.set("location[directory]", directory)
  const body = method === "GET" || method === "HEAD" ? undefined : "{"
  return handler(
    new Request(url, { method, headers: { ...headers, "content-type": "application/json" }, body }),
  ).then(async (response) => ({ status: response.status, body: await response.text() }))
}

const isLockdownRefusal = (result: { status: number; body: string }) =>
  result.status === 403 && result.body.includes("SpecTuner does not allow")

describe("lock-down rules", () => {
  test("every route is either refused or explicitly allowed", () => {
    const unclassified = endpoints()
      .filter(({ method, path }) => path !== FILE_READ && ServerLockdown.refused(method, path, new URL("http://x/")) === undefined)
      .map(({ method, path }) => `${method} ${path}`)
      .filter((key) => !ALLOWED.has(key))
    expect(unclassified).toEqual([])
  })

  test("every rule names a route that exists, and nothing allowed is refused", () => {
    const known = endpoints()
    const stale = ServerLockdown.RULES.filter(
      (rule) => !known.some((endpoint) => endpoint.path === rule.path && (rule.method === "*" || rule.method === endpoint.method)),
    )
    expect(stale).toEqual([])
    const both = [...ALLOWED].filter((key) => {
      const [method, path] = key.split(" ")
      return ServerLockdown.refused(method, path, new URL("http://x/")) !== undefined
    })
    expect(both).toEqual([])
  })

  test("the setting is on only for 1 or true", () => {
    expect(ServerLockdown.enabled({})).toBe(false)
    expect(ServerLockdown.enabled({ SPECTUNER_LOCKDOWN: "0" })).toBe(false)
    expect(ServerLockdown.enabled({ SPECTUNER_LOCKDOWN: "1" })).toBe(true)
    expect(ServerLockdown.enabled({ SPECTUNER_LOCKDOWN: "true" })).toBe(true)
  })

  test("file reads pass for image types only", () => {
    const read = (path: string) => ServerLockdown.refused("GET", FILE_READ, new URL(`http://x/api/fs/read/${path}`))
    expect(read("shot.png")).toBeUndefined()
    expect(read("dir/Photo.JPG")).toBeUndefined()
    expect(read("spectuner.json")).toBe("file-read")
    expect(read("..%2Fsecret.txt")).toBe("file-read")
    expect(read("secret.txt%3Ax.png")).toBe("file-read")
    expect(read("secret.txt%00.png")).toBe("file-read")
    expect(read("noextension")).toBe("file-read")
    expect(read("%E0%A4%A")).toBe("file-read")
  })
})

describe("SpecTuner server (lock-down on)", () => {
  it.live("refuses every rule's route with 403, before the handler runs", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      const handler = yield* serve("1")
      const methods = (path: string) => endpoints().filter((endpoint) => endpoint.path === path).map((e) => e.method)
      const cases = ServerLockdown.RULES.flatMap((rule) =>
        (rule.method === "*" ? methods(rule.path) : [rule.method]).map((method) => ({ method, path: rule.path, family: rule.family })),
      )
      const results = yield* Effect.promise(() =>
        Promise.all(
          cases.map(async (item) => ({ ...item, result: await send(handler, item.method, concrete(item.path), tmp.path) })),
        ),
      )
      const notRefused = results.filter((item) => !isLockdownRefusal(item.result))
      expect(notRefused.map((item) => `${item.method} ${item.path} -> ${item.result.status}`)).toEqual([])
      const shell = results.find((item) => item.path === "/api/session/:sessionID/shell")
      expect(JSON.parse(shell?.result.body ?? "{}")).toEqual({
        _tag: "ForbiddenError",
        message: "SpecTuner does not allow running commands (shell).",
      })
      // The families the lock-down promises are all present.
      expect(new Set(cases.map((item) => item.family))).toEqual(
        new Set([
          "shell",
          "terminal",
          "command",
          "automation",
          "file-write",
          "file-read",
          "tool-server",
          "plugin",
          "config",
          "remote-access",
          "network",
        ]),
      )
    }).pipe(Effect.scoped),
  )

  it.live("refuses ticketed terminal and pairing paths that skip the password check", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      const handler = yield* serve("1")
      const results = yield* Effect.promise(() =>
        Promise.all([
          send(handler, "GET", "/api/pty/x/connect?ticket=abc", tmp.path, {}),
          send(handler, "GET", "/api/experimental/persistent-pty/x/connect?ticket=abc", tmp.path, {}),
          send(handler, "GET", "/api/experimental/client-mcp?ticket=abc", tmp.path, {}),
          send(handler, "GET", "/auth/connect/abc", tmp.path, {}),
        ]),
      )
      expect(results.map(isLockdownRefusal)).toEqual([true, true, true, true])
    }).pipe(Effect.scoped),
  )

  it.live("refuses even when the server runs without a password", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      const handler = yield* serve("1", "none")
      const result = yield* Effect.promise(() => send(handler, "POST", "/api/session/x/shell", tmp.path, {}))
      expect(isLockdownRefusal(result)).toBe(true)
      const info = yield* Effect.promise(() => send(handler, "GET", "/api/info", tmp.path, {}))
      expect(info.status).toBe(200)
    }).pipe(Effect.scoped),
  )

  it.live("still checks the password first for requests that need one", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      const handler = yield* serve("1")
      const allowed = yield* Effect.promise(() => send(handler, "GET", "/api/session", tmp.path, {}))
      expect(allowed.status).toBe(401)
    }).pipe(Effect.scoped),
  )

  it.live("serves image reads and the routes the app uses", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      yield* Effect.promise(async () => {
        await Bun.write(`${tmp.path}/shot.png`, new Uint8Array([137, 80, 78, 71]))
        await Bun.write(`${tmp.path}/notes.txt`, "secret")
      })
      const handler = yield* serve("1")
      const [image, text, info, sessions] = yield* Effect.promise(() =>
        Promise.all([
          send(handler, "GET", "/api/fs/read/shot.png", tmp.path),
          send(handler, "GET", "/api/fs/read/notes.txt", tmp.path),
          send(handler, "GET", "/api/info", tmp.path),
          send(handler, "GET", "/api/session", tmp.path),
        ]),
      )
      expect(image.status).toBe(200)
      expect(isLockdownRefusal(text)).toBe(true)
      expect(info.status).toBe(200)
      expect(sessions.status).toBe(200)
    }).pipe(Effect.scoped),
  )
})

describe("server without the setting", () => {
  it.live("serves the same routes as before: none gets the lock-down refusal", () =>
    Effect.gen(function* () {
      const tmp = yield* tmpdirScoped()
      yield* Effect.promise(() => Bun.write(`${tmp.path}/notes.txt`, "plain"))
      const handler = yield* serve(undefined)
      // Requests that cannot change anything: a malformed body, unknown ids, reads and lists.
      const results = yield* Effect.promise(() =>
        Promise.all([
          send(handler, "POST", "/api/session/x/shell", tmp.path),
          send(handler, "GET", "/api/shell", tmp.path),
          send(handler, "GET", "/api/pty", tmp.path),
          send(handler, "POST", "/api/experimental/fs/write", tmp.path),
          send(handler, "GET", "/api/fs/read/notes.txt", tmp.path),
          send(handler, "PUT", "/api/experimental/mcp/x", tmp.path),
          send(handler, "PATCH", "/api/experimental/config", tmp.path),
          send(handler, "POST", "/api/plugin/update", tmp.path),
          send(handler, "POST", "/api/websearch", tmp.path),
          send(handler, "POST", "/api/experimental/generate/text", tmp.path),
          send(handler, "POST", "/api/session/x/command", tmp.path),
        ]),
      )
      expect(results.filter(isLockdownRefusal)).toEqual([])
      expect(results[1].status).toBe(200)
      expect(results[4]).toEqual({ status: 200, body: "plain" })
    }).pipe(Effect.scoped),
  )
})
