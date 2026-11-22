// A scripted stand-in for a real model, for trying `script/e2e.ts` without one:
//   bun run script/scripted-model.ts --config <file.json>
// Writes a provider-config file for `e2e.ts --provider-config` (provider "scripted", model "scripted-model") and
// serves until Ctrl+C. Per prompt it calls one tool, then answers: game_changes when the prompt mentions "change",
// game_detect otherwise.
import { writeFileSync } from "node:fs"
import path from "node:path"
import { parseArgs } from "node:util"
import { Scripted } from "./scripted.ts"

const args = parseArgs({ options: { config: { type: "string" } } }).values
if (!args.config) throw new Error("--config is required")

const model = Scripted.model(Scripted.perPrompt)
writeFileSync(path.resolve(args.config), JSON.stringify({ providers: Scripted.providers(model.url) }, null, 2))
console.log(`scripted model at ${model.url}; provider config written to ${path.resolve(args.config)}`)
console.log(`use: --provider-config ${path.resolve(args.config)} --model ${Scripted.PROVIDER}/${Scripted.MODEL}`)
process.on("SIGINT", () => void model.stop().then(() => process.exit(0)))
await new Promise(() => {})
