export * as Scripted from "./scripted.ts"

// A scripted model for SpecTuner checks: a tiny local OpenAI-compatible endpoint that records every request it
// receives (including the `tools` array the server offers) and answers with scripted replies. It is wired in as a
// provider through the server's writable global config file, the same place the player's own provider settings
// live, and never through the product config document.
//
// Pattern from packages/cli/test/acp/subprocess.ts (fake /v1/chat/completions over SSE).
import { mkdirSync, writeFileSync } from "node:fs"
import path from "node:path"
import { SpecTunerEnv } from "../src/env.ts"
import { SpecTunerServer } from "./server.ts"

export const PROVIDER = "scripted"
export const MODEL = "scripted-model"

export interface ChatRequest {
  readonly model?: string
  readonly messages: readonly { readonly role: string; readonly content?: unknown; readonly tool_call_id?: string }[]
  readonly tools?: readonly { readonly type: string; readonly function: { readonly name: string } }[]
}

/** One reply: plain text, or a single tool call. */
export type Reply = { readonly text: string } | { readonly tool: string; readonly input: unknown }

export interface Model {
  readonly url: string
  /** Every request the server sent, in order. */
  readonly requests: ChatRequest[]
  readonly stop: () => Promise<void>
}

/**
 * Starts the endpoint. `reply` decides the answer to each agent request; title requests (recognised by the title
 * agent's system prompt) are answered with a fixed title.
 */
export function model(reply: (request: ChatRequest, index: number) => Reply): Model {
  const requests: ChatRequest[] = []
  const agent = { count: 0 }
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port: 0,
    async fetch(request) {
      if (request.method !== "POST" || !new URL(request.url).pathname.endsWith("/chat/completions"))
        return new Response("Not found", { status: 404 })
      const body = (await request.json()) as ChatRequest
      requests.push(body)
      if (isTitle(body)) return stream({ text: "Game check" })
      if (isCompaction(body)) return stream({ text: "## Objective\n- Check the player's game." })
      return stream(reply(body, agent.count++))
    },
  })
  return {
    url: `http://127.0.0.1:${server.port}/v1`,
    requests,
    stop: () => server.stop(true),
  }
}

export function isTitle(request: ChatRequest) {
  return request.messages.some(
    (message) => message.role === "system" && JSON.stringify(message.content).includes("You name conversations"),
  )
}

/** Compaction requests end with the summary prompt; the server checks the reply uses the template headings. */
export function isCompaction(request: ChatRequest) {
  const last = request.messages.at(-1)
  return last?.role === "user" && JSON.stringify(last.content).includes("summarize the conversation above")
}

/** The default script: call `game_detect` once, then finish with a sentence. */
export function detectOnce(request: ChatRequest): Reply {
  return request.messages.some((message) => message.role === "tool")
    ? { text: "I checked your game." }
    : { tool: "game_detect", input: {} }
}

/**
 * A multi-prompt script: per prompt, one tool call, then a sentence. The tool is game_benchmark with trySettings if
 * the prompt mentions "try", game_changes if it mentions "change", otherwise game_detect.
 */
export function perPrompt(request: ChatRequest): Reply {
  const last = request.messages.at(-1)
  if (last?.role === "tool") return { text: "I looked at your game and everything is in order." }
  const text = JSON.stringify(last?.content ?? "").toLowerCase()
  if (text.includes("try"))
    return {
      tool: "game_benchmark",
      // `pairs` is not a tool input: like the real model that sent it, it must be dropped before the sidecar.
      input: { label: "try: shorter shadows", trySettings: { shadowDistance: 20 }, pairs: 2 },
    }
  return text.includes("change") ? { tool: "game_changes", input: {} } : { tool: "game_detect", input: {} }
}

/** The `providers` block for the scripted model at `url`. */
export function providers(url: string) {
  return {
    [PROVIDER]: {
      name: "Scripted",
      package: "@opencode/ai/providers/openai-compatible",
      settings: { apiKey: "scripted-key", baseURL: url },
      models: {
        [MODEL]: {
          name: "Scripted Model",
          capabilities: { tools: true, input: ["text"], output: ["text"] },
          cost: { input: 0, output: 0 },
          limit: { context: 100_000, output: 10_000 },
        },
      },
    },
  }
}

/** Writes the scripted provider into the server's global config file (`<home>/config/spectuner/spectuner.json`). */
export function configure(home: string, url: string) {
  const dir = path.join(SpecTunerEnv.dirs(home).config, "spectuner")
  mkdirSync(dir, { recursive: true })
  writeFileSync(
    path.join(dir, "spectuner.json"),
    JSON.stringify({ model: `${PROVIDER}/${MODEL}`, providers: providers(url) }, null, 2),
  )
}

export interface RunOptions extends SpecTunerServer.Options {
  /** The session folder (the game). */
  readonly game: string
  readonly prompt: string
  readonly reply?: (request: ChatRequest, index: number) => Reply
  /** After the prompt, also compact the session and wait again. */
  readonly compact?: boolean
}

export interface ToolRecord {
  readonly type: "tool"
  readonly name: string
  readonly state: {
    readonly status: string
    readonly input?: unknown
    readonly content?: unknown
    readonly error?: unknown
  }
}

interface Message {
  readonly type: string
  readonly agent?: string
  readonly content?: readonly { readonly type: string }[]
}

export interface Run {
  readonly session: { readonly id: string; readonly title: string }
  /** Agents that answered, from the session's assistant messages (the session itself only names an agent if one was picked). */
  readonly agents: readonly string[]
  /** Tool calls recorded in the session, as `{ name, state: { status, input, content?, error? } }`. */
  readonly tools: readonly ToolRecord[]
  /** Agent requests only (title and compaction requests left out). */
  readonly requests: ChatRequest[]
  /** Title requests. */
  readonly titles: ChatRequest[]
  /** Compaction requests. */
  readonly compactions: ChatRequest[]
  /** Message types in the session, oldest first (a successful compaction adds a "compaction" message). */
  readonly types: readonly string[]
  readonly server: SpecTunerServer.Server
}

/**
 * Starts the scripted model and a SpecTuner server, opens a session in `game` with the default agent, sends one
 * prompt and waits for the session to go idle. The caller stops the server and model via the returned `stop`.
 */
export async function run(options: RunOptions) {
  const fake = model(options.reply ?? detectOnce)
  configure(options.home, fake.url)
  const server = await SpecTunerServer.start(options).catch(async (error) => {
    await fake.stop()
    throw error
  })
  const stop = async () => {
    await server.stop()
    await fake.stop()
  }
  try {
    const result = await converse(server, fake, options)
    return { ...result, stop }
  } catch (error) {
    await stop()
    throw new Error(
      `${error instanceof Error ? error.message : String(error)}\n--- server output ---\n${server.output()}`,
    )
  }
}

async function converse(server: SpecTunerServer.Server, fake: Model, options: RunOptions): Promise<Run> {
  const query = SpecTunerServer.location(options.game)
  const json = async (response: Response) => {
    if (!response.ok) throw new Error(`${response.status} ${response.url}: ${await response.text()}`)
    return response.status === 204 ? undefined : response.json()
  }
  // Plugins load asynchronously; wait until the game tools, the modder agent and the scripted model are there.
  await SpecTunerServer.until(async () => {
    const plugins = await json(await server.fetch(`/api/plugin?${query}`))
    const agents = await json(await server.fetch(`/api/agent?${query}`))
    const models = await json(await server.fetch(`/api/model?${query}`))
    return (
      plugins.data.some(
        (plugin: { id: string; state: { status: string } }) =>
          plugin.id === "spectuner.modder" && plugin.state.status === "active",
      ) &&
      agents.data.some((agent: { id: string }) => agent.id === "modder") &&
      models.data.some((model: { id: string }) => model.id === MODEL)
    )
  }, 60_000)
  const created = await json(await server.fetch("/api/session", { body: { location: { directory: options.game } } }))
  const id = String(created.data.id)
  await json(await server.fetch(`/api/session/${id}/prompt`, { body: { text: options.prompt } }))
  await json(await server.fetch(`/api/experimental/session/${id}/wait`, { method: "POST" }))
  if (options.compact) {
    await json(await server.fetch(`/api/session/${id}/compact`, { body: {} }))
    await json(await server.fetch(`/api/experimental/session/${id}/wait`, { method: "POST" }))
  }
  const messages: Message[] = (await json(await server.fetch(`/api/session/${id}/message?limit=200&order=asc`))).data
  // Titles are written in the background after the first prompt.
  const title = await SpecTunerServer.until(async () => {
    const info = await json(await server.fetch(`/api/session/${id}`))
    return typeof info.data.title === "string" && info.data.title !== "" && info.data.title
  }, 15_000).catch(() => "")
  const assistant = messages.filter((message) => message.type === "assistant")
  return {
    session: { id, title },
    agents: [...new Set(assistant.map((message) => String(message.agent)))],
    tools: assistant
      .flatMap((message) => message.content ?? [])
      .filter((item): item is ToolRecord => item.type === "tool"),
    requests: fake.requests.filter((request) => !isTitle(request) && !isCompaction(request)),
    titles: fake.requests.filter(isTitle),
    compactions: fake.requests.filter(isCompaction),
    types: messages.map((message) => message.type),
    server,
  }
}

function stream(reply: Reply) {
  const chunks =
    "text" in reply
      ? [
          { choices: [{ delta: { role: "assistant" }, finish_reason: null }], usage: null },
          { choices: [{ delta: { content: reply.text }, finish_reason: null }], usage: null },
          { choices: [{ delta: {}, finish_reason: "stop" }], usage: null },
        ]
      : [
          {
            choices: [
              {
                delta: {
                  role: "assistant",
                  tool_calls: [
                    {
                      index: 0,
                      id: `call_${crypto.randomUUID().slice(0, 8)}`,
                      type: "function",
                      function: { name: reply.tool, arguments: "" },
                    },
                  ],
                },
                finish_reason: null,
              },
            ],
            usage: null,
          },
          {
            choices: [{ delta: { tool_calls: [{ index: 0, function: { arguments: JSON.stringify(reply.input) } }] } }],
            usage: null,
          },
          { choices: [{ delta: {}, finish_reason: "tool_calls" }], usage: null },
        ]
  const usage = { choices: [], usage: { prompt_tokens: 10, completion_tokens: 1, total_tokens: 11 } }
  return new Response(
    `${[...chunks, usage].map((chunk) => `data: ${JSON.stringify(chunk)}\n\n`).join("")}data: [DONE]\n\n`,
    {
      headers: { "content-type": "text/event-stream" },
    },
  )
}
