export * as SpecTunerE2E from "./e2e.ts"

// End-to-end driver: one real conversation with the modder agent through a SpecTuner server's HTTP API, with live
// progress on stdout and a transcript at the end. See `bun run script/e2e.ts --help`.
import { spawnSync } from "node:child_process"
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { parseArgs } from "node:util"
import { SpecTunerEnv } from "../src/env.ts"
import { SpecTunerServer } from "./server.ts"

export const HARNESS_DIR = path.resolve(SpecTunerServer.PACKAGE_DIR, "..", "..", "..")
export const STAGED_CLI = path.resolve(
  SpecTunerServer.PACKAGE_DIR,
  "..",
  "desktop",
  "resources",
  process.platform === "win32" ? "spectuner-server.exe" : "spectuner-server",
)

export interface Options {
  readonly game: string
  readonly prompts: readonly string[]
  /** `provider/model`, optionally `#variant`. */
  readonly model: string
  /** Parsed provider-config document (JSONC already parsed). */
  readonly providerConfig?: Record<string, unknown>
  /** `integrationID -> environment variable name` holding the API key. */
  readonly keys?: Readonly<Record<string, string>>
  /** Compiled server binary; undefined runs the CLI from source. */
  readonly exe?: string
  readonly sidecar: string
  readonly pluginDir: string
  readonly home: string
  readonly port?: number
  readonly out: string
  readonly turnTimeoutMs: number
  readonly recordTools: boolean
  /** Extra variables for the server process (tests use this for the fake sidecar). */
  readonly env?: Readonly<Record<string, string>>
  /** Where progress lines go. Default stdout. */
  readonly print?: (line: string) => void
}

export interface ToolStep {
  readonly kind: "tool"
  readonly id: string
  readonly name: string
  readonly input: unknown
  readonly status: string
  /** The full tool result text the model received. */
  readonly result?: string
  readonly error?: string
  readonly startedAt?: string
  readonly completedAt?: string
  readonly durationMs?: number
}

export type Step =
  | { readonly kind: "text"; readonly text: string; readonly at?: string }
  | ToolStep
  | { readonly kind: "permission"; readonly request: unknown; readonly decision: "reject" }
  | { readonly kind: "error"; readonly message: string }

export interface Turn {
  readonly prompt: string
  readonly startedAt: string
  readonly finishedAt: string
  readonly durationMs: number
  readonly outcome: "succeeded" | "failed" | "interrupted" | "timeout" | "error"
  readonly error?: string
  readonly steps: readonly Step[]
  readonly answer: string
}

export interface ModelRequest {
  readonly at: string
  readonly provider: string
  readonly method: string
  readonly path: string
  readonly status?: number
  /** Tool names offered in the request body; null when the body had no `tools` array. */
  readonly tools: readonly string[] | null
}

export interface Transcript {
  readonly startedAt: string
  readonly finishedAt: string
  readonly server: { readonly kind: "exe" | "source"; readonly path: string; readonly version: string }
  readonly game: string
  readonly sidecar: string
  readonly pluginDir: string
  readonly model: string
  readonly agents: readonly string[]
  readonly selectableAgents: readonly string[]
  readonly sessionID: string
  readonly turns: readonly Turn[]
  readonly toolCalls: Readonly<Record<string, number>>
  readonly modelRequests?: readonly ModelRequest[]
  readonly toolsOffered?: readonly string[]
  readonly ok: boolean
  readonly error?: string
}

/** Runs the whole conversation and writes the transcript. Resolves with the transcript; never leaves a server behind. */
export async function run(options: Options): Promise<Transcript> {
  const startedAt = new Date()
  const secrets = Object.values(options.keys ?? {}).flatMap((name) => {
    const value = process.env[name]
    return value ? [value] : []
  })
  const clean = cleaner(secrets)
  const print = (line: string) => (options.print ?? console.log)(clean(line))
  const model = parseModel(options.model)
  const version = serverVersion(options)
  print(`server: ${options.exe ? `exe ${options.exe}` : "source (packages/cli/src/index.ts)"} version ${version}`)

  const missing = Object.entries(options.keys ?? {}).filter((entry) => !process.env[entry[1]])
  if (missing.length > 0)
    throw new Error(`environment variable not set: ${missing.map((entry) => entry[1]).join(", ")}`)

  mkdirSync(options.home, { recursive: true })
  mkdirSync(options.out, { recursive: true })
  const recorder = options.recordTools ? startRecorder() : undefined
  writePlayerConfig(options, recorder)

  const state = {
    agents: [] as string[],
    selectable: [] as string[],
    sessionID: "",
    turns: [] as Turn[],
    error: undefined as string | undefined,
  }
  const server = await SpecTunerServer.start({
    home: options.home,
    sidecar: options.sidecar,
    pluginDir: options.pluginDir,
    exe: options.exe,
    port: options.port,
    env: {
      ...(process.env.SPECTUNER_HOME ? {} : { SPECTUNER_HOME: path.join(options.home, "spectuner") }),
      ...options.env,
    },
  }).catch((error) => {
    recorder?.stop()
    throw error
  })
  const stopAll = async () => {
    await server.stop()
    await recorder?.stop()
  }
  const interrupt = () => void stopAll().then(() => process.exit(130))
  process.once("SIGINT", interrupt)
  try {
    print(`server listening on ${server.url} (home ${options.home})`)
    const api = client(server)
    const query = SpecTunerServer.location(options.game)

    state.selectable = await waitForAgents(api, query)
    print(`selectable agents: ${state.selectable.join(", ")}`)
    if (state.selectable.join(",") !== "modder")
      throw new Error(`expected modder to be the only selectable agent, found: ${state.selectable.join(", ")}`)

    for (const [integration, name] of Object.entries(options.keys ?? {})) {
      // Integrations load with the Location's plugins; connect only once this one is listed.
      await SpecTunerServer.until(async () => {
        const list = await api.call(`/api/integration?${query}`)
        return list.data.some((item: { id: string }) => item.id === integration)
      }, 60_000).catch(() => {
        throw new Error(`integration "${integration}" was not found on the server`)
      })
      await api.call(`/api/integration/${encodeURIComponent(integration)}/connect/key?${query}`, {
        body: { key: process.env[name] ?? "" },
      })
      print(`stored the API key for ${integration} from $${name}`)
    }
    await waitForModel(api, query, model)

    const created = await api.call("/api/session", {
      body: {
        location: { directory: options.game },
        model: { providerID: model.providerID, id: model.id, ...(model.variant ? { variant: model.variant } : {}) },
      },
    })
    state.sessionID = String(created.data.id)
    print(`session ${state.sessionID} in ${options.game} with model ${options.model}`)

    for (const prompt of options.prompts) {
      const turn = await runTurn(api, state.sessionID, prompt, options.turnTimeoutMs, print)
      state.turns.push(turn)
      if (turn.outcome !== "succeeded") {
        state.error = `turn ${state.turns.length} ${turn.outcome}${turn.error ? `: ${turn.error}` : ""}`
        break
      }
    }
    state.agents = await answeringAgents(api, state.sessionID)
  } catch (error) {
    state.error = error instanceof Error ? error.message : String(error)
    print(`error: ${state.error}`)
  } finally {
    process.off("SIGINT", interrupt)
    await stopAll()
  }

  const toolCalls = state.turns
    .flatMap((turn) => turn.steps)
    .filter((step): step is ToolStep => step.kind === "tool")
    .reduce<Record<string, number>>((counts, step) => ({ ...counts, [step.name]: (counts[step.name] ?? 0) + 1 }), {})
  const requests = recorder?.requests
  const transcript: Transcript = {
    startedAt: startedAt.toISOString(),
    finishedAt: new Date().toISOString(),
    server: { kind: options.exe ? "exe" : "source", path: options.exe ?? SpecTunerServer.CLI_DIR, version },
    game: options.game,
    sidecar: options.sidecar,
    pluginDir: options.pluginDir,
    model: options.model,
    agents: state.agents,
    selectableAgents: state.selectable,
    sessionID: state.sessionID,
    turns: state.turns,
    toolCalls,
    ...(requests
      ? {
          modelRequests: requests,
          toolsOffered: [...new Set(requests.flatMap((request) => request.tools ?? []))].sort(),
        }
      : {}),
    ok: state.error === undefined && state.turns.length === options.prompts.length,
    ...(state.error ? { error: state.error } : {}),
  }
  writeFileSync(path.join(options.out, "transcript.json"), clean(JSON.stringify(transcript, null, 2)))
  writeFileSync(path.join(options.out, "transcript.md"), clean(markdown(transcript)))
  summary(transcript).forEach(print)
  print(`transcript: ${path.join(options.out, "transcript.json")} and transcript.md`)
  return transcript
}

// ---------------------------------------------------------------------------------------------------------------
// Turns

interface Message {
  readonly id: string
  readonly type: string
  readonly agent?: string
  readonly outcome?: string
  readonly error?: { readonly message?: string; readonly type?: string }
  readonly content?: readonly Content[]
  readonly time: { readonly created: number; readonly completed?: number }
}

type Content =
  | { readonly type: "text"; readonly text: string }
  | {
      readonly type: "tool"
      readonly id: string
      readonly name: string
      readonly state: {
        readonly status: string
        readonly input?: unknown
        readonly content?: readonly { readonly type: string; readonly text?: string }[]
        readonly error?: { readonly message?: string }
      }
      readonly time: { readonly created: number; readonly ran?: number; readonly completed?: number }
    }
  | { readonly type: "reasoning" }

async function runTurn(
  api: Client,
  sessionID: string,
  prompt: string,
  timeoutMs: number,
  print: (line: string) => void,
): Promise<Turn> {
  const before = new Set((await messages(api, sessionID)).map((message) => message.id))
  const started = Date.now()
  const printed = new Set<string>()
  const permissions: Step[] = []
  print(``)
  print(`> ${prompt}`)
  await api.call(`/api/session/${sessionID}/prompt`, { body: { text: prompt } })

  const finish = (fresh: readonly Message[], outcome: Turn["outcome"], error?: string): Turn => {
    report(fresh, printed, print, true)
    const steps = [...permissions, ...stepsOf(fresh)]
    const answer = steps.findLast((step) => step.kind === "text")
    const finished = Date.now()
    print(`< turn ${outcome} in ${Math.round((finished - started) / 1000)}s${error ? `: ${error}` : ""}`)
    return {
      prompt,
      startedAt: new Date(started).toISOString(),
      finishedAt: new Date(finished).toISOString(),
      durationMs: finished - started,
      outcome,
      ...(error ? { error } : {}),
      steps,
      answer: answer?.kind === "text" ? answer.text : "",
    }
  }

  while (true) {
    const fresh = (await messages(api, sessionID)).filter((message) => !before.has(message.id))
    report(fresh, printed, print, false)
    const pending = await api.call(`/api/session/${sessionID}/permission`)
    for (const request of pending.data as { id: string }[]) {
      permissions.push({ kind: "permission", request, decision: "reject" })
      print(`  ! permission request ${JSON.stringify(request).slice(0, 200)} -> denied`)
      await api.call(`/api/session/${sessionID}/permission/${request.id}/reply`, {
        body: { decision: "reject", message: "Not allowed in SpecTuner." },
      })
    }
    const idle = fresh.find((message) => message.type === "idle")
    if (idle) {
      const outcome = idle.outcome === "succeeded" || idle.outcome === "interrupted" ? idle.outcome : "failed"
      const failure = idle.error?.message ?? fresh.findLast((message) => message.error)?.error?.message
      return finish(fresh, outcome, outcome === "succeeded" ? undefined : failure)
    }
    if (Date.now() - started > timeoutMs) {
      await api.call(`/api/session/${sessionID}/interrupt`, { body: {} }).catch(() => undefined)
      return finish(fresh, "timeout", `no answer within ${Math.round(timeoutMs / 60_000)} minutes`)
    }
    await Bun.sleep(750)
  }
}

/** Prints progress lines for what is new: tool starts, tool ends, and assistant text once its message completed. */
function report(fresh: readonly Message[], printed: Set<string>, print: (line: string) => void, final: boolean) {
  for (const message of fresh) {
    if (message.type !== "assistant") continue
    for (const [index, item] of (message.content ?? []).entries()) {
      if (item.type === "tool") {
        if (!printed.has(`start:${item.id}`) && item.state.status !== "streaming") {
          printed.add(`start:${item.id}`)
          print(`  - ${item.name} ${summarizeInput(item.state.input)}`)
        }
        if (!printed.has(`end:${item.id}`) && (item.state.status === "completed" || item.state.status === "error")) {
          printed.add(`end:${item.id}`)
          const seconds = item.time.completed
            ? ` ${((item.time.completed - item.time.created) / 1000).toFixed(1)}s`
            : ""
          print(`    ${item.name} ${item.state.status}${seconds}: ${outcomeOf(item.state)}`)
        }
      }
      if (item.type === "text" && item.text.trim() && (message.time.completed || final)) {
        const key = `text:${message.id}:${index}`
        if (printed.has(key)) continue
        printed.add(key)
        print(item.text.trim())
      }
    }
    if (message.error && !printed.has(`error:${message.id}`)) {
      printed.add(`error:${message.id}`)
      print(`  ! model error: ${message.error.message ?? message.error.type ?? "unknown"}`)
    }
  }
}

function stepsOf(fresh: readonly Message[]): Step[] {
  return fresh.flatMap((message): Step[] => {
    if (message.type !== "assistant") return []
    const items = (message.content ?? []).flatMap((item): Step[] => {
      if (item.type === "text")
        return item.text.trim()
          ? [{ kind: "text", text: item.text, at: iso(message.time.completed ?? message.time.created) }]
          : []
      if (item.type !== "tool") return []
      const result = (item.state.content ?? []).map((part) => part.text ?? `[${part.type}]`).join("\n")
      return [
        {
          kind: "tool",
          id: item.id,
          name: item.name,
          input: item.state.input,
          status: item.state.status,
          ...(result ? { result } : {}),
          ...(item.state.error?.message ? { error: item.state.error.message } : {}),
          startedAt: iso(item.time.created),
          ...(item.time.completed
            ? { completedAt: iso(item.time.completed), durationMs: item.time.completed - item.time.created }
            : {}),
        },
      ]
    })
    return message.error ? [...items, { kind: "error", message: message.error.message ?? "model error" }] : items
  })
}

/** The sidecar envelope's summary, or `code: message` when it refused; tool errors as they are. */
function outcomeOf(state: Extract<Content, { type: "tool" }>["state"]) {
  if (state.status === "error") return state.error?.message ?? "error"
  const text = (state.content ?? []).map((part) => part.text ?? "").join("")
  const envelope = parseJson(text)
  if (!isRecord(envelope)) return text.slice(0, 160)
  if (envelope.ok === true) return String(envelope.summary ?? "ok")
  const error = isRecord(envelope.error) ? envelope.error : {}
  return `${String(error.code ?? "ERROR")}: ${String(error.message ?? "")}`
}

/** One line of input: long strings (mod source) are shortened to their length. */
function summarizeInput(input: unknown) {
  if (!isRecord(input) || Object.keys(input).length === 0) return ""
  const short = Object.fromEntries(
    Object.entries(input).map(([key, value]) => [
      key,
      typeof value === "string" && value.length > 60 ? `<${value.length} chars>` : value,
    ]),
  )
  const text = JSON.stringify(short)
  return text.length > 200 ? `${text.slice(0, 197)}...` : text
}

// ---------------------------------------------------------------------------------------------------------------
// Server API

interface Client {
  // Response bodies are JSON of many shapes; callers read the fields they need.
  readonly call: (route: string, init?: { method?: string; body?: unknown }) => Promise<any>
}

function client(server: SpecTunerServer.Server): Client {
  return {
    call: async (route, init) => {
      const response = await server.fetch(route, init)
      if (!response.ok)
        throw new Error(
          `${init?.body === undefined ? "GET" : "POST"} ${route.split("?")[0]}: ${response.status} ${await response.text()}`,
        )
      return response.status === 204 ? undefined : response.json()
    },
  }
}

async function messages(api: Client, sessionID: string): Promise<Message[]> {
  // Newest 200, oldest first: enough for the current turn even in a long session.
  const page = await api.call(`/api/session/${sessionID}/message?limit=200&order=desc`)
  return [...page.data].reverse()
}

async function answeringAgents(api: Client, sessionID: string) {
  const all = await messages(api, sessionID)
  return [...new Set(all.filter((message) => message.type === "assistant").map((message) => String(message.agent)))]
}

/** Waits for the game tools and agents to load, then returns the selectable agents (not hidden, not subagent). */
async function waitForAgents(api: Client, query: string): Promise<string[]> {
  const selectable = async (): Promise<string[]> => {
    const agents = await api.call(`/api/agent?${query}`)
    return agents.data
      .filter((agent: { hidden: boolean; mode: string }) => !agent.hidden && agent.mode !== "subagent")
      .map((agent: { id: string }) => agent.id)
  }
  await SpecTunerServer.until(async () => {
    const plugins = await api.call(`/api/plugin?${query}`)
    const loaded = plugins.data.find((plugin: { id: string }) => plugin.id === "spectuner.modder")
    if (loaded?.state?.status === "failed")
      throw new Error(`the SpecTuner plugin failed to load: ${JSON.stringify(loaded.state)}`)
    return loaded?.state?.status === "active" && (await selectable()).length > 0
  }, 60_000)
  // While plugins finish loading, an agent from a config file can show for a moment before the plugin's lock removes
  // it. Give that 15 seconds to settle; whatever is still selectable then is the answer.
  return SpecTunerServer.until(async () => {
    const list = await selectable()
    return list.join(",") === "modder" && list
  }, 15_000).catch(() => selectable())
}

async function waitForModel(api: Client, query: string, model: { providerID: string; id: string }) {
  await SpecTunerServer.until(async () => {
    const models = await api.call(`/api/model?${query}`)
    return models.data.some(
      (item: { providerID: string; id: string }) => item.providerID === model.providerID && item.id === model.id,
    )
  }, 60_000).catch(() => {
    throw new Error(
      `model ${model.providerID}/${model.id} is not available on the server (check --provider-config or --key-env)`,
    )
  })
}

// ---------------------------------------------------------------------------------------------------------------
// Player config and the tool recorder

/**
 * Writes the provider config into the isolated home's writable config file, where a player's own settings live. The
 * inline lock-down document outranks this file. With a recorder, every provider `baseURL` is pointed at a proxy.
 */
function writePlayerConfig(options: Options, recorder: Recorder | undefined) {
  const dir = path.join(SpecTunerEnv.dirs(options.home).config, "spectuner")
  mkdirSync(dir, { recursive: true })
  const config = structuredClone(options.providerConfig ?? {})
  if (recorder) {
    // V2 `providers.<id>.settings.baseURL`, and the V1 spelling `provider.<id>.options.baseURL`.
    for (const [block, field] of [
      ["providers", "settings"],
      ["provider", "options"],
    ] as const) {
      const providers = config[block]
      if (!isRecord(providers)) continue
      for (const [id, provider] of Object.entries(providers)) {
        const settings = isRecord(provider) ? provider[field] : undefined
        if (!isRecord(settings) || typeof settings.baseURL !== "string") continue
        settings.baseURL = recorder.proxy(id, settings.baseURL)
      }
    }
  }
  writeFileSync(path.join(dir, "spectuner.json"), JSON.stringify(config, null, 2))
}

interface Recorder {
  readonly requests: ModelRequest[]
  /** Starts a pass-through proxy for one provider and returns the base URL to use instead. */
  readonly proxy: (provider: string, baseURL: string) => string
  readonly stop: () => Promise<void>
}

function startRecorder(): Recorder {
  const requests: ModelRequest[] = []
  const servers: { stop: (force?: boolean) => Promise<void> | void }[] = []
  return {
    requests,
    proxy: (provider, baseURL) => {
      const target = new URL(baseURL)
      const server = Bun.serve({
        hostname: "127.0.0.1",
        port: 0,
        // Local models can think for minutes before the first byte; never cut a connection for being idle.
        idleTimeout: 0,
        async fetch(request) {
          const url = new URL(request.url)
          const body = request.method === "GET" || request.method === "HEAD" ? undefined : await request.text()
          const parsed = body ? parseJson(body) : undefined
          const tools =
            isRecord(parsed) && Array.isArray(parsed.tools)
              ? parsed.tools.map((tool: unknown) =>
                  isRecord(tool) && isRecord(tool.function)
                    ? String(tool.function.name)
                    : isRecord(tool)
                      ? String(tool.name)
                      : "?",
                )
              : null
          const record = { at: new Date().toISOString(), provider, method: request.method, path: url.pathname, tools }
          const headers = new Headers(request.headers)
          headers.delete("host")
          headers.delete("content-length")
          const upstream = await fetch(new URL(`${url.pathname}${url.search}`, target.origin), {
            method: request.method,
            headers,
            body,
            redirect: "manual",
            signal: request.signal,
          }).catch((error) => {
            requests.push({ ...record, status: 502 })
            throw error
          })
          requests.push({ ...record, status: upstream.status })
          // fetch already decoded the body, so the encoding and length headers no longer describe it.
          const out = new Headers(upstream.headers)
          out.delete("content-encoding")
          out.delete("content-length")
          // Passing the body stream through keeps server-sent events unbuffered.
          return new Response(upstream.body, { status: upstream.status, statusText: upstream.statusText, headers: out })
        },
      })
      servers.push(server)
      return `http://127.0.0.1:${server.port}${target.pathname.replace(/\/$/, "")}${baseURL.endsWith("/") ? "/" : ""}`
    },
    stop: async () => {
      await Promise.all(servers.map((server) => server.stop(true)))
    },
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Output

function summary(transcript: Transcript) {
  const counts = Object.entries(transcript.toolCalls)
  return [
    "",
    "=== summary ===",
    `result: ${transcript.ok ? "ok" : `FAILED (${transcript.error ?? "incomplete"})`}`,
    `session agent: ${transcript.agents.join(", ") || "(none answered)"}`,
    `model: ${transcript.model}`,
    `turns: ${transcript.turns.map((turn) => `${turn.outcome} ${Math.round(turn.durationMs / 1000)}s`).join(", ")}`,
    ...(transcript.modelRequests
      ? [
          `model requests recorded: ${transcript.modelRequests.length}`,
          `tool names offered: ${transcript.toolsOffered?.join(", ") || "(none)"}`,
        ]
      : []),
    `tool calls: ${counts.length ? counts.map(([name, count]) => `${name} x${count}`).join(", ") : "(none)"}`,
  ]
}

function markdown(transcript: Transcript) {
  const lines = [
    `# SpecTuner e2e transcript`,
    ``,
    `- Result: ${transcript.ok ? "ok" : `FAILED (${transcript.error ?? "incomplete"})`}`,
    `- Model: ${transcript.model}`,
    `- Agent: ${transcript.agents.join(", ")}`,
    `- Server: ${transcript.server.kind} ${transcript.server.path} (${transcript.server.version})`,
    `- Game: ${transcript.game}`,
    `- Session: ${transcript.sessionID}`,
    `- Started ${transcript.startedAt}, finished ${transcript.finishedAt}`,
    ...(transcript.toolsOffered
      ? [`- Tools offered (${transcript.modelRequests?.length} requests): ${transcript.toolsOffered.join(", ")}`]
      : []),
    ``,
  ]
  transcript.turns.forEach((turn, index) => {
    lines.push(
      `## Prompt ${index + 1}: ${turn.prompt}`,
      ``,
      `${turn.outcome}, ${Math.round(turn.durationMs / 1000)}s${turn.error ? `: ${turn.error}` : ""}`,
      ``,
    )
    turn.steps.forEach((step, number) => {
      if (step.kind === "text") return lines.push(`${number + 1}. Said: ${step.text.trim().replaceAll("\n", "\n   ")}`)
      if (step.kind === "permission")
        return lines.push(`${number + 1}. Permission request denied: \`${JSON.stringify(step.request)}\``)
      if (step.kind === "error") return lines.push(`${number + 1}. Model error: ${step.message}`)
      lines.push(
        `${number + 1}. Tool \`${step.name}\` ${step.status}${step.durationMs === undefined ? "" : ` in ${(step.durationMs / 1000).toFixed(1)}s`}`,
        `   - input: \`${summarizeInput(step.input) || "{}"}\``,
        `   - result: ${step.error ? step.error : outcomeOf({ status: step.status, content: [{ type: "text", text: step.result ?? "" }] })}`,
      )
    })
    lines.push(``, `**Final answer:**`, ``, turn.answer || "(none)", ``)
  })
  return lines.join("\n")
}

/** Replaces secrets with [redacted] and the user-profile folder with %USERPROFILE% (raw, forward-slash and JSON-escaped). */
export function cleaner(secrets: readonly string[]) {
  const profile = os.homedir()
  const variants = [profile, profile.replaceAll("\\", "/"), profile.replaceAll("\\", "\\\\")]
  const pattern = new RegExp(variants.map((value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|"), "gi")
  return (text: string) =>
    secrets.reduce((result, secret) => result.split(secret).join("[redacted]"), text).replace(pattern, "%USERPROFILE%")
}

function serverVersion(options: Options) {
  if (!options.exe) {
    const pkg = JSON.parse(readFileSync(path.join(SpecTunerServer.CLI_DIR, "package.json"), "utf8"))
    return `${pkg.version} (source)`
  }
  const result = spawnSync(options.exe, ["--version"], {
    env: SpecTunerEnv.make({ home: options.home, sidecar: options.sidecar, pluginDir: options.pluginDir }),
    encoding: "utf8",
    timeout: 60_000,
    windowsHide: true,
  })
  return result.stdout.trim() || `unknown (exit ${result.status})`
}

/** A provider-config file: JSON or JSONC holding one object. */
export function readConfig(file: string) {
  const parsed: unknown = Bun.JSONC.parse(readFileSync(file, "utf8"))
  if (!isRecord(parsed)) throw new Error(`${file} must contain one JSON object`)
  return parsed
}

function parseModel(value: string) {
  const [ref, variant] = value.split("#")
  const slash = ref.indexOf("/")
  if (slash <= 0 || slash === ref.length - 1) throw new Error(`--model must be provider/model, got "${value}"`)
  return { providerID: ref.slice(0, slash), id: ref.slice(slash + 1), variant }
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return undefined
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}

function iso(millis: number | undefined) {
  return millis === undefined ? undefined : new Date(millis).toISOString()
}

// ---------------------------------------------------------------------------------------------------------------
// CLI

const USAGE = `bun run script/e2e.ts --game DIR --model PROVIDER/MODEL --prompt TEXT [--prompt TEXT ...]
  [--provider-config FILE.json|.jsonc] [--key-env INTEGRATION=ENV_NAME ...] [--record-tools]
  [--exe PATH] [--sidecar PATH] [--plugin-dir DIR] [--home DIR] [--port N] [--out DIR] [--turn-timeout-minutes N]`

if (import.meta.main) {
  const args = parseArgs({
    options: {
      game: { type: "string" },
      prompt: { type: "string", multiple: true },
      model: { type: "string" },
      "provider-config": { type: "string" },
      "key-env": { type: "string", multiple: true },
      exe: { type: "string" },
      sidecar: { type: "string" },
      "plugin-dir": { type: "string" },
      home: { type: "string" },
      port: { type: "string" },
      out: { type: "string" },
      "turn-timeout-minutes": { type: "string" },
      "record-tools": { type: "boolean" },
      help: { type: "boolean" },
    },
  }).values
  if (args.help || !args.game || !args.model || !args.prompt?.length) {
    console.log(USAGE)
    process.exit(args.help ? 0 : 2)
  }
  const home = path.resolve(
    args.home ?? path.join(HARNESS_DIR, "tmp", "e2e", new Date().toISOString().replace(/[:.]/g, "-").replace("Z", "")),
  )
  const keys = Object.fromEntries(
    (args["key-env"] ?? []).map((entry) => {
      const index = entry.indexOf("=")
      if (index <= 0) throw new Error(`--key-env must be INTEGRATION=ENV_NAME, got "${entry}"`)
      return [entry.slice(0, index), entry.slice(index + 1)]
    }),
  )
  const exe = args.exe ? path.resolve(args.exe) : existsSync(STAGED_CLI) ? STAGED_CLI : undefined
  const configFile = args["provider-config"]
  const transcript = await run({
    game: path.resolve(args.game),
    prompts: args.prompt,
    model: args.model,
    providerConfig: configFile ? readConfig(path.resolve(configFile)) : undefined,
    keys,
    exe,
    sidecar: path.resolve(args.sidecar ?? SpecTunerServer.DEFAULT_SIDECAR),
    pluginDir: path.resolve(args["plugin-dir"] ?? SpecTunerServer.PLUGIN_DIR),
    home,
    port: args.port ? Number(args.port) : undefined,
    out: path.resolve(args.out ?? home),
    turnTimeoutMs: Number(args["turn-timeout-minutes"] ?? 45) * 60_000,
    recordTools: args["record-tools"] ?? false,
  }).catch((error) => {
    console.error(cleaner([])(`e2e failed: ${error instanceof Error ? error.message : String(error)}`))
    process.exit(1)
  })
  process.exit(transcript.ok ? 0 : 1)
}
