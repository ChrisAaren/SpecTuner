export * as SpecTunerLocalModel from "./local-model.ts"

import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { Option, Schema } from "effect"
import { SpecTunerJsonc } from "./jsonc.ts"
import type { RpcDomain } from "@spectuner/plugin/promise/rpc"
import { SpecTunerRpc } from "./rpc.ts"

// "Use a local model": a player points SpecTuner at an OpenAI-compatible server on their own machine (llama.cpp,
// vLLM, Jan, ...). The server, not the window, asks that address for its models, so browser cross-origin rules do
// not apply; it asks only `<address>/models` and that server's `/props`. Saving writes one provider entry into the
// server's own writable config file, which the server reloads by itself.

/** The provider id SpecTuner owns in the config file. */
export const PROVIDER = SpecTunerRpc.LOCAL_PROVIDER

/**
 * The context window assumed when the server does not report one. Small local models are often served with a 4k
 * to 8k window; assuming a huge one makes long requests overflow instead of being compacted in time. 8192 tokens
 * holds the modder's instructions and tools with room for a few steps.
 */
export const DEFAULT_CONTEXT = 8192

/**
 * The most output asked for in one reply. The output limit also caps a compaction summary (the core sends
 * `min(limit.output, room left)` as its max tokens), and a summary cut short fails the compaction, so a model with a
 * large window gets room for a full summary. A quarter of the window keeps small windows for the conversation.
 * Not higher: a small model that starts repeating itself runs until this limit, and 8192 tokens is already more
 * than a minute of wasted time on a home GPU. A mod is a few hundred tokens.
 */
export const MAX_OUTPUT = 8192

/** How long a local server gets to answer. */
export const TIMEOUT_MS = 5000

/** Stored as the key when the player gave none: the OpenAI-compatible client needs a value, local servers ignore it. */
const NO_KEY = "spectuner-no-key"

export interface Address {
  /** The base URL requests go to, such as `http://127.0.0.1:8080/v1`. */
  readonly base: string
  readonly models: string
  /** llama.cpp's server properties (`n_ctx` is the context window it was started with). */
  readonly props: string
}

/** The address as a base URL, or undefined unless it is a plain `http://` or `https://` URL. */
export function address(input: string): Address | undefined {
  const trimmed = input.trim().replace(/\/+$/, "")

  if (!URL.canParse(trimmed)) return undefined

  const url = new URL(trimmed)

  if (url.protocol !== "http:" && url.protocol !== "https:") return undefined

  if (url.username || url.password || url.search || url.hash) return undefined

  const base = url.href.replace(/\/+$/, "")

  return { base, models: `${base}/models`, props: new URL("/props", url).href }
}

const ModelsResponse = Schema.Struct({
  data: Schema.Array(
    Schema.Struct({
      id: Schema.String,
      context_length: Schema.optional(Schema.Number),
      max_context_length: Schema.optional(Schema.Number),
      // llama.cpp's router: the command line each model is started with, which holds the window it is served with.
      status: Schema.optional(Schema.Struct({ args: Schema.optional(Schema.Array(Schema.String)) })),
    }),
  ),
})

const PropsResponse = Schema.Struct({
  role: Schema.optional(Schema.String),
  n_ctx: Schema.optional(Schema.Number),
  default_generation_settings: Schema.optional(Schema.Struct({ n_ctx: Schema.optional(Schema.Number) })),
})

const decodeModels = Schema.decodeUnknownOption(ModelsResponse)

const decodeProps = Schema.decodeUnknownOption(PropsResponse)

export interface FindInput {
  readonly address: string
  readonly key?: string
  readonly signal?: AbortSignal
  readonly timeoutMs?: number
}

/** The models the server at `address` offers, each with the context window it reports, if any. */
export async function find(input: FindInput): Promise<SpecTunerRpc.LocalFind> {
  const target = address(input.address)

  if (!target) return { ok: false, reason: "invalid-address", address: input.address.trim(), models: [] }

  const get = (url: string) =>
    fetch(url, {
      headers: input.key ? { authorization: `Bearer ${input.key}` } : {},
      // A redirect could point anywhere; only the address the player typed is asked.
      redirect: "manual",
      signal: AbortSignal.any([
        AbortSignal.timeout(input.timeoutMs ?? TIMEOUT_MS),
        ...(input.signal ? [input.signal] : []),
      ]),
    }).catch(() => undefined)

  const response = await get(target.models)
  const failed = (reason: SpecTunerRpc.LocalFindFailure) => ({ ok: false, reason, address: target.base, models: [] })

  if (!response) return failed("unreachable")

  if (response.status === 401 || response.status === 403) return failed("rejected")

  if (!response.ok) return failed("not-compatible")

  const listed = Option.getOrUndefined(decodeModels(await response.json().catch(() => undefined)))

  if (!listed) return failed("not-compatible")

  if (listed.data.length === 0) return failed("no-models")

  const served = await servedContext(await get(target.props))

  return {
    ok: true,
    reason: null,
    address: target.base,
    models: listed.data.map((model) => ({
      id: model.id,
      // `context_length` is often the model's trained maximum, larger than the window it is served with.
      context:
        served ??
        positive(ctxSize(model.status?.args)) ??
        positive(model.context_length) ??
        positive(model.max_context_length) ??
        null,
    })),
  }
}

async function servedContext(response: Response | undefined) {
  if (!response?.ok) return undefined

  const props = Option.getOrUndefined(decodeProps(await response.json().catch(() => undefined)))

  // llama.cpp's router serves several models, each with its own window, and reports 0 here.
  if (!props || props.role === "router") return undefined

  return positive(props.n_ctx ?? props.default_generation_settings?.n_ctx)
}

/** llama.cpp's `--ctx-size N`, `--ctx-size=N`, or `-c N` (the last one given wins, as on its command line). */
function ctxSize(args: readonly string[] | undefined) {
  const values = (args ?? []).flatMap((arg, index, all) => {
    const joined = /^--ctx-size=(.*)$/.exec(arg)

    if (joined) return [joined[1]]

    return arg === "--ctx-size" || arg === "-c" ? [all[index + 1] ?? ""] : []
  })
  const last = values.at(-1)

  // 0 means the model's own maximum, which `context_length` reports.
  return last !== undefined && /^\d+$/.test(last) ? Number(last) : undefined
}

function positive(value: number | undefined) {
  return value !== undefined && Number.isInteger(value) && value > 0 ? value : undefined
}

export interface Choice {
  readonly address: Address
  readonly key?: string
  readonly model: string
  /** The window the server reported; `DEFAULT_CONTEXT` when it reported none. */
  readonly context?: number
}

/** The provider entry for the chosen model, in the shape the server's OpenAI-compatible providers use. */
export function entry(choice: Choice) {
  const context = positive(choice.context) ?? DEFAULT_CONTEXT

  return {
    name: "Local model",
    package: "@opencode/ai/providers/openai-compatible",
    settings: { baseURL: choice.address.base, apiKey: choice.key || NO_KEY },
    models: {
      [choice.model]: {
        name: choice.model,
        capabilities: { tools: true, input: ["text"], output: ["text"] },
        cost: { input: 0, output: 0 },
        limit: { context, output: Math.min(MAX_OUTPUT, Math.floor(context / 4)) },
      },
    },
  }
}

/** The config text with the local model saved and chosen as the default model. Everything else is kept. */
export function withLocalModel(text: string, choice: Choice) {
  const saved = SpecTunerJsonc.set(text, ["providers", PROVIDER], entry(choice))

  return SpecTunerJsonc.set(saved, ["model"], `${PROVIDER}/${choice.model}`)
}

/** The config text without the local model, and without the default model when it was the local one. */
export function withoutLocalModel(text: string) {
  const removed = SpecTunerJsonc.set(text, ["providers", PROVIDER], undefined)
  const chosen = parse(SpecTunerJsonc.read(removed, ["model"]))

  if (typeof chosen === "string" && chosen.startsWith(`${PROVIDER}/`))
    return SpecTunerJsonc.set(removed, ["model"], undefined)

  return removed
}

const Saved = Schema.Struct({
  settings: Schema.Struct({ baseURL: Schema.String, apiKey: Schema.optional(Schema.String) }),
  models: Schema.Record(Schema.String, Schema.Unknown),
})

const decodeSaved = Schema.decodeUnknownOption(Saved)

/** The saved local model, if any. */
export function current(text: string): SpecTunerRpc.LocalCurrent | null {
  if (!text.trim()) return null

  const saved = Option.getOrUndefined(decodeSaved(parse(SpecTunerJsonc.read(text, ["providers", PROVIDER]))))
  const model = saved && Object.keys(saved.models)[0]

  if (!saved || !model) return null

  return { address: saved.settings.baseURL, model, hasKey: !!saved.settings.apiKey && saved.settings.apiKey !== NO_KEY }
}

function parse(json: string | undefined): unknown {
  if (json === undefined) return undefined

  return Option.getOrUndefined(Schema.decodeUnknownOption(Schema.fromJsonString(Schema.Unknown))(json))
}

/**
 * The server's global config file, as the server picks it: `SPECTUNER_CONFIG_DIR`, else `$XDG_CONFIG_HOME/spectuner`;
 * the last of `spectuner.json` and `spectuner.jsonc` that exists, else a new `spectuner.jsonc`.
 */
export function configFile(env: Readonly<Record<string, string | undefined>> = process.env) {
  const dir =
    env.SPECTUNER_CONFIG_DIR ?? path.join(env.XDG_CONFIG_HOME || path.join(os.homedir(), ".config"), "spectuner")

  return (
    ["spectuner.json", "spectuner.jsonc"]
      .map((name) => path.join(dir, name))
      .filter((file) => existsSync(file))
      .at(-1) ?? path.join(dir, "spectuner.jsonc")
  )
}

/** Applies `edit` to the config file; a file that is not a JSONC object is left alone. */
export function update(file: string, edit: (text: string) => string): SpecTunerRpc.LocalResult {
  const text = existsSync(file) ? readFileSync(file, "utf8") : ""
  const next = tryEdit(text, edit)

  if (next === undefined) return { ok: false, reason: "unreadable" }

  if (next === text) return { ok: true, reason: null }

  mkdirSync(path.dirname(file), { recursive: true })
  // Written beside the file and moved over it, so the server never reads half a file.
  const partial = `${file}.${process.pid}.partial`
  writeFileSync(partial, next)
  renameSync(partial, file)

  return { ok: true, reason: null }
}

function tryEdit(text: string, edit: (text: string) => string) {
  try {
    return edit(text)
  } catch (error) {
    if (error instanceof SpecTunerJsonc.JsoncError) return undefined
    throw error
  }
}

/** Serves the "Use a local model" RPC. Location-independent: every Location's instance edits the same global file. */
export function register(rpc: RpcDomain) {
  return rpc.register(SpecTunerRpc.LocalDefinition, {
    get: async () => current(existsSync(configFile()) ? readFileSync(configFile(), "utf8") : ""),
    find: (input, context) => find({ address: input.address, key: input.key, signal: context.signal }),
    save: async (input): Promise<SpecTunerRpc.LocalResult> => {
      const target = address(input.address)

      if (!target) return { ok: false, reason: "invalid-address" }

      if (!input.model.trim()) return { ok: false, reason: "invalid-model" }

      const choice = { address: target, key: input.key, model: input.model, context: input.context ?? undefined }

      return update(configFile(), (text) => withLocalModel(text, choice))
    },
    remove: async () => update(configFile(), withoutLocalModel),
  })
}
