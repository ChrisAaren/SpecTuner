import { afterAll, beforeAll, describe, expect, test } from "bun:test"
import { mkdtempSync, readFileSync, rmSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { SpecTunerJsonc } from "../plugin/jsonc.ts"
import { SpecTunerLocalModel } from "../plugin/local-model.ts"

// A throwaway OpenAI-compatible endpoint: `/v1/models`, llama.cpp's `/props`, and a few broken variants.
const seen: string[] = []

const server = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    const url = new URL(request.url)
    seen.push(`${url.pathname} ${request.headers.get("authorization") ?? ""}`)

    if (url.pathname === "/v1/models")
      return Response.json({ object: "list", data: [{ id: "qwen-small", object: "model" }, { id: "other" }] })

    if (url.pathname === "/props") return Response.json({ default_generation_settings: { n_ctx: 4096 } })

    if (url.pathname === "/locked/models") return new Response("no", { status: 401 })

    if (url.pathname === "/html/models") return new Response("<html>hello</html>")

    if (url.pathname === "/empty/models") return Response.json({ data: [] })

    if (url.pathname === "/moved/models") return Response.redirect("http://example.com/models", 302)

    return new Response("not found", { status: 404 })
  },
})

const base = `http://127.0.0.1:${server.port}`

// A server without llama.cpp's /props, whose model list carries the window.
const plain = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    seen.push(`${new URL(request.url).pathname} ${request.headers.get("authorization") ?? ""}`)

    if (new URL(request.url).pathname === "/v1/models")
      return Response.json({ data: [{ id: "lm", context_length: 32768 }] })

    return new Response("not found", { status: 404 })
  },
})

// llama.cpp's server in router mode, in the shape it reports them (made-up values): `/props` reports no window, and
// each model's `context_length` is its trained maximum; the served window is `--ctx-size` in `status.args`.
const routed = (id: string, args: string[], extra: Record<string, unknown> = {}) => ({
  id,
  object: "model",
  owned_by: "llamacpp",
  status: { value: "unloaded", args: ["llama-server.exe", "--host", "127.0.0.1", ...args], preset: "" },
  source: "preset",
  ...extra,
})

const router = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    const url = new URL(request.url)
    seen.push(url.pathname + url.search)

    if (url.pathname === "/v1/models")
      return Response.json({
        data: [
          routed(
            "example-12b",
            ["--jinja", "--port", "8081", "--alias", "example-12b", "--ctx-size", "65536", "--model", "models\\example.gguf"],
            { context_length: 262144 },
          ),
          routed("gpt-oss-20b", ["--alias", "gpt-oss-20b", "--ctx-size", "32768", "--n-predict", "8192"], {
            context_length: 131072,
          }),
          routed("joined", ["--ctx-size=24576", "--n-gpu-layers", "auto"], { context_length: 131072 }),
          routed("short-flag", ["-c", "16384"], { context_length: 131072 }),
          routed("model-maximum", ["--ctx-size", "0"], { context_length: 40960 }),
          routed("no-args", [], { context_length: 20480 }),
          { id: "bare", object: "model", max_context_length: 12288 },
          { id: "nothing", object: "model" },
        ],
      })

    if (url.pathname === "/props")
      return Response.json({
        role: "router",
        max_instances: 1,
        models_autoload: true,
        model_alias: "llama-server",
        model_path: "none",
        default_generation_settings: { params: null, n_ctx: 0 },
      })

    return new Response("not found", { status: 404 })
  },
})

// A single-model llama.cpp server whose model list also carries a (trained) maximum: `/props` wins.
const single = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    const url = new URL(request.url)

    if (url.pathname === "/v1/models")
      return Response.json({ data: [{ id: "one", context_length: 131072, status: { args: ["--ctx-size", "999"] } }] })

    if (url.pathname === "/props") return Response.json({ n_ctx: 32768 })

    return new Response("not found", { status: 404 })
  },
})

// A server whose `/props` reports 0 without saying it is a router: the model list is used instead.
const zero = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    const url = new URL(request.url)

    if (url.pathname === "/v1/models") return Response.json({ data: [{ id: "z", context_length: 8192 * 3 }] })

    if (url.pathname === "/props") return Response.json({ default_generation_settings: { n_ctx: 0 } })

    return new Response("not found", { status: 404 })
  },
})

const state = { dir: "" }

beforeAll(() => {
  state.dir = mkdtempSync(path.join(os.tmpdir(), "spectuner-local-model-"))
})

afterAll(() => {
  void server.stop(true)
  void plain.stop(true)
  void router.stop(true)
  void single.stop(true)
  void zero.stop(true)
  rmSync(state.dir, { recursive: true, force: true })
})

describe("address", () => {
  const rows: { input: string; base?: string }[] = [
    { input: "http://127.0.0.1:8080/v1", base: "http://127.0.0.1:8080/v1" },
    { input: "  https://ai.local/v1/  ", base: "https://ai.local/v1" },
    { input: "http://localhost:11434", base: "http://localhost:11434" },
    { input: "file:///C:/Windows/win.ini" },
    { input: "ftp://127.0.0.1/v1" },
    { input: "javascript:alert(1)" },
    { input: "http://user:secret@127.0.0.1:8080/v1" },
    { input: "http://127.0.0.1:8080/v1?x=1" },
    { input: "127.0.0.1:8080" },
    { input: "" },
  ]

  test.each(rows)("$input", (row) => {
    expect(SpecTunerLocalModel.address(row.input)?.base).toBe(row.base)
  })

  test("asks only <address>/models and that server's /props", () => {
    expect(SpecTunerLocalModel.address("http://127.0.0.1:8080/v1")).toEqual({
      base: "http://127.0.0.1:8080/v1",
      models: "http://127.0.0.1:8080/v1/models",
      props: "http://127.0.0.1:8080/props",
    })
  })
})

describe("find", () => {
  test("lists the models with the context window llama.cpp reports, sending the key", async () => {
    seen.length = 0
    const found = await SpecTunerLocalModel.find({ address: `${base}/v1/`, key: "abc" })

    expect(found).toEqual({
      ok: true,
      reason: null,
      address: `${base}/v1`,
      models: [
        { id: "qwen-small", context: 4096 },
        { id: "other", context: 4096 },
      ],
    })
    expect(seen).toEqual(["/v1/models Bearer abc", "/props Bearer abc"])
  })

  test("uses a per-model context window when the server has no /props, and sends no key when none is given", async () => {
    seen.length = 0
    const found = await SpecTunerLocalModel.find({ address: `http://127.0.0.1:${plain.port}/v1` })

    expect(found.models).toEqual([{ id: "lm", context: 32768 }])
    expect(seen).toEqual(["/v1/models ", "/props "])
  })

  test("llama.cpp's router: each model gets the window it is served with, not its trained maximum", async () => {
    seen.length = 0
    const found = await SpecTunerLocalModel.find({ address: `http://127.0.0.1:${router.port}/v1` })

    expect(found).toMatchObject({ ok: true, reason: null })
    expect(found.models).toEqual([
      { id: "example-12b", context: 65536 },
      { id: "gpt-oss-20b", context: 32768 },
      { id: "joined", context: 24576 },
      { id: "short-flag", context: 16384 },
      // `--ctx-size 0` serves the model's maximum.
      { id: "model-maximum", context: 40960 },
      { id: "no-args", context: 20480 },
      { id: "bare", context: 12288 },
      { id: "nothing", context: null },
    ])
    // Never `/props?model=...`: on a router that loads the model and evicts the one in use.
    expect(seen).toEqual(["/v1/models", "/props"])
  })

  test("a single-model server's /props window wins over the model list", async () => {
    const found = await SpecTunerLocalModel.find({ address: `http://127.0.0.1:${single.port}/v1` })

    expect(found.models).toEqual([{ id: "one", context: 32768 }])
  })

  test("a /props window of 0 is ignored", async () => {
    const found = await SpecTunerLocalModel.find({ address: `http://127.0.0.1:${zero.port}/v1` })

    expect(found.models).toEqual([{ id: "z", context: 24576 }])
  })

  const failures: { name: string; address: string; reason: string }[] = [
    { name: "a non-http address", address: "file:///C:/Windows", reason: "invalid-address" },
    { name: "nothing listening", address: "http://127.0.0.1:9/v1", reason: "unreachable" },
    { name: "a refused key", address: `${base}/locked`, reason: "rejected" },
    { name: "an answer that is not a model list", address: `${base}/html`, reason: "not-compatible" },
    { name: "a 404", address: `${base}/nothing`, reason: "not-compatible" },
    { name: "a redirect elsewhere, which is not followed", address: `${base}/moved`, reason: "not-compatible" },
    { name: "no models", address: `${base}/empty`, reason: "no-models" },
  ]

  test.each(failures)("$name gives $reason", async (row) => {
    const found = await SpecTunerLocalModel.find({ address: row.address, timeoutMs: 2000 })

    expect(found).toMatchObject({ ok: false, reason: row.reason, models: [] })
  })
})

describe("config file", () => {
  const choice = (model: string, context?: number) => ({
    address: SpecTunerLocalModel.address("http://127.0.0.1:8080/v1")!,
    model,
    context,
  })

  const existing = `{
  // The player's own settings stay as they are.
  "theme": "dark",
  "providers": {
    /* another provider */
    "scripted": { "name": "Scripted", "settings": { "baseURL": "http://127.0.0.1:1/v1" } },
  },
}
`

  test("merges into a file with comments and other providers, keeping every other byte", () => {
    const saved = SpecTunerLocalModel.withLocalModel(existing, choice("qwen-small", 4096))

    expect(saved).toContain("// The player's own settings stay as they are.")
    expect(saved).toContain("/* another provider */")
    expect(saved).toContain('"scripted": { "name": "Scripted", "settings": { "baseURL": "http://127.0.0.1:1/v1" } },')
    expect(SpecTunerLocalModel.current(saved)).toEqual({
      address: "http://127.0.0.1:8080/v1",
      model: "qwen-small",
      hasKey: false,
    })
    expect(JSON.parse(SpecTunerJsonc.read(saved, ["providers", "spectuner-local"])!)).toEqual(
      SpecTunerLocalModel.entry(choice("qwen-small", 4096)),
    )
    expect(JSON.parse(SpecTunerJsonc.read(saved, ["model"])!)).toBe("spectuner-local/qwen-small")
    // Removing again gives back the original text.
    expect(SpecTunerLocalModel.withoutLocalModel(saved)).toBe(existing)
  })

  test("updates the saved model in place", () => {
    const first = SpecTunerLocalModel.withLocalModel(existing, choice("qwen-small"))
    const second = SpecTunerLocalModel.withLocalModel(first, { ...choice("other", 2048), key: "k" })

    expect(SpecTunerLocalModel.current(second)).toEqual({
      address: "http://127.0.0.1:8080/v1",
      model: "other",
      hasKey: true,
    })
    expect(second.match(/spectuner-local"/g)?.length).toBe(1)
    expect(
      JSON.parse(SpecTunerJsonc.read(second, ["providers", "spectuner-local", "models", "other", "limit"])!),
    ).toEqual({
      context: 2048,
      output: 512,
    })
  })

  test("a server that reports no window gets the conservative default", () => {
    expect(SpecTunerLocalModel.entry(choice("m")).models.m.limit).toEqual({
      context: SpecTunerLocalModel.DEFAULT_CONTEXT,
      output: 2048,
    })
  })

  test("a large window leaves room for a full compaction summary, up to MAX_OUTPUT", () => {
    expect(SpecTunerLocalModel.entry(choice("m", 32768)).models.m.limit).toEqual({ context: 32768, output: 8192 })
    expect(SpecTunerLocalModel.entry(choice("m", 65536)).models.m.limit).toEqual({
      context: 65536,
      output: SpecTunerLocalModel.MAX_OUTPUT,
    })
  })

  test("an empty file becomes a small document; removing from it changes nothing", () => {
    const saved = SpecTunerLocalModel.withLocalModel("", choice("m"))

    expect(JSON.parse(saved)).toEqual({
      providers: { "spectuner-local": SpecTunerLocalModel.entry(choice("m")) },
      model: "spectuner-local/m",
    })
    expect(SpecTunerLocalModel.withoutLocalModel("")).toBe("")
  })

  test("removing keeps a default model the player chose elsewhere", () => {
    const own = `{ "model": "anthropic/claude", "providers": { "spectuner-local": { "settings": { "baseURL": "x" }, "models": { "m": {} } } } }\n`

    expect(JSON.parse(SpecTunerLocalModel.withoutLocalModel(own))).toEqual({ model: "anthropic/claude", providers: {} })
  })

  test("a file that is not a JSONC object is left alone; a missing one is created", async () => {
    const file = path.join(state.dir, "broken", "spectuner.jsonc")
    const missing = path.join(state.dir, "new", "spectuner.jsonc")
    await Bun.write(file, "{ this is not json")
    const save = (text: string) => SpecTunerLocalModel.withLocalModel(text, choice("m"))

    expect(SpecTunerLocalModel.update(file, save)).toEqual({ ok: false, reason: "unreadable" })
    expect(readFileSync(file, "utf8")).toBe("{ this is not json")
    expect(SpecTunerLocalModel.update(missing, save)).toEqual({ ok: true, reason: null })
    expect(SpecTunerLocalModel.current(readFileSync(missing, "utf8"))?.model).toBe("m")
  })

  test("the config file is the server's: the config dir override, else XDG_CONFIG_HOME/spectuner", () => {
    expect(SpecTunerLocalModel.configFile({ SPECTUNER_CONFIG_DIR: state.dir })).toBe(
      path.join(state.dir, "spectuner.jsonc"),
    )
    expect(SpecTunerLocalModel.configFile({ XDG_CONFIG_HOME: path.join(state.dir, "none") })).toBe(
      path.join(state.dir, "none", "spectuner", "spectuner.jsonc"),
    )
  })
})

describe("jsonc edits", () => {
  const rows: { name: string; text: string; path: string[]; value: unknown; expected: string }[] = [
    {
      name: "adds a member to an empty object",
      text: "{}\n",
      path: ["a"],
      value: 1,
      expected: '{\n  "a": 1\n}\n',
    },
    {
      name: "keeps a trailing comma style",
      text: '{\n  "a": 1,\n}\n',
      path: ["b"],
      value: 2,
      expected: '{\n  "a": 1,\n  "b": 2,\n}\n',
    },
    {
      name: "removes a middle member on one line without touching its neighbours",
      text: '{ "a": 1, "b": 2, "c": 3 }',
      path: ["b"],
      value: undefined,
      expected: '{ "a": 1, "c": 3 }',
    },
    {
      name: "removes the last member and its comma",
      text: '{\n  "a": 1,\n  "b": 2\n}',
      path: ["b"],
      value: undefined,
      expected: '{\n  "a": 1\n}',
    },
    {
      name: "creates a missing parent object",
      text: '{ "x": "// not a comment" }',
      path: ["p", "q"],
      value: true,
      expected: '{ "x": "// not a comment",\n  "p": {\n    "q": true\n  } }',
    },
  ]

  test.each(rows)("$name", (row) => {
    expect(SpecTunerJsonc.set(row.text, row.path, row.value)).toBe(row.expected)
  })
})
