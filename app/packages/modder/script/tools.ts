// Prints the tools the modder agent is actually offered, taken from a real model request:
//   bun run script/tools.ts --game DIR [--home DIR] [--exe SERVER-PROGRAM] [--sidecar SIDECAR-PROGRAM] [--plugin DIR]
//                           [--check]
// A scripted local model records the request the server sends for a new session in DIR with the default agent,
// calls game_detect once, and finishes. Without --home a fresh folder under the system temp dir is used and removed.
// --plugin loads the tool plugin from DIR (a packaged app's `resources/spectuner/plugin`) instead of the source.
// --check exits with an error unless game_detect was offered and the sidecar's answer reached the model: a check that
// a built server program, plugin and sidecar work together.
import { mkdtempSync, rmSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { parseArgs } from "node:util"
import { Scripted } from "./scripted.ts"
import { SpecTunerServer } from "./server.ts"

const args = parseArgs({
  options: {
    game: { type: "string" },
    home: { type: "string" },
    exe: { type: "string" },
    sidecar: { type: "string" },
    plugin: { type: "string" },
    check: { type: "boolean" },
  },
}).values

if (!args.game) throw new Error("--game is required")

const home = args.home ? path.resolve(args.home) : mkdtempSync(path.join(os.tmpdir(), "spectuner-tools-"))
const result = await Scripted.run({
  home,
  game: path.resolve(args.game),
  exe: args.exe && path.resolve(args.exe),
  sidecar: path.resolve(args.sidecar ?? process.env.SPECTUNER_SIDECAR ?? SpecTunerServer.DEFAULT_SIDECAR),
  pluginDir: args.plugin && path.resolve(args.plugin),
  prompt: "Check my game.",
})
await result.stop()
if (!args.home) rmSync(home, { recursive: true, force: true })

const first = result.requests[0]
const names = (first?.tools ?? []).map((tool) => tool.function.name)
const toolMessage = result.requests.flatMap((request) => request.messages).find((message) => message.role === "tool")
console.log(`server: ${args.exe ? path.resolve(args.exe) : "source (bun run packages/cli/src/index.ts)"}`)
console.log(`session title: ${result.session.title}`)
console.log(`agent that answered: ${result.agents.join(", ")}`)
console.log(`model requests seen: ${result.requests.length}`)
console.log(`tools offered in the first request (${names.length}):`)
names.forEach((name) => console.log(`  ${name}`))
console.log(
  `tool calls recorded in the session: ${result.tools.map((tool) => `${tool.name} (${tool.state.status})`).join(", ")}`,
)
console.log(`game_detect result sent back to the model:`)
console.log(
  `  ${typeof toolMessage?.content === "string" ? toolMessage.content : JSON.stringify(toolMessage?.content)}`,
)

if (args.check) {
  const answer = typeof toolMessage?.content === "string" ? toolMessage.content : JSON.stringify(toolMessage?.content ?? "")
  const problems = [
    ...(names.includes("game_detect") ? [] : ["game_detect was not offered to the model"]),
    ...(names.some((name) => !name.startsWith("game_")) ? ["a tool other than game_* was offered"] : []),
    // The plugin sends the sidecar's envelope back without its `command` field (plugin/sidecar.ts `format`).
    ...(/"ok"\s*:/.test(answer) ? [] : ["the sidecar's answer did not reach the model"]),
  ]
  if (problems.length) {
    console.error(`CHECK FAILED: ${problems.join("; ")}`)
    process.exit(1)
  }
  console.log("CHECK OK: the server offered the game tools and the sidecar answered game_detect")
}
