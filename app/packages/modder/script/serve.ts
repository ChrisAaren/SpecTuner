// Starts a SpecTuner server for development:
//   bun run script/serve.ts --port 4096 [--home DIR] [--exe path\to\spectuner.exe] [--sidecar path\to\spectuner-sidecar.exe]
// Without --exe the CLI runs from source. Without --home the server uses %LOCALAPPDATA%\SpecTuner\app.
// Prints the URL and password, then runs until Ctrl+C.
import path from "node:path"
import { parseArgs } from "node:util"
import { SpecTunerEnv } from "../src/env.ts"
import { SpecTunerServer } from "./server.ts"

const args = parseArgs({
  options: {
    port: { type: "string" },
    home: { type: "string" },
    exe: { type: "string" },
    sidecar: { type: "string" },
    plugin: { type: "string" },
  },
}).values

if (!args.port) throw new Error("--port is required")

const server = await SpecTunerServer.start({
  port: Number(args.port),
  home: path.resolve(args.home ?? SpecTunerEnv.defaultHome()),
  exe: args.exe && path.resolve(args.exe),
  sidecar: path.resolve(args.sidecar ?? process.env.SPECTUNER_SIDECAR ?? SpecTunerServer.DEFAULT_SIDECAR),
  pluginDir: args.plugin && path.resolve(args.plugin),
  echo: true,
})
console.log(`SpecTuner server ${server.url} (pid ${server.pid}) user spectuner password ${server.password}`)

const shutdown = () => server.stop().then(() => process.exit(0))
process.on("SIGINT", shutdown)
process.on("SIGTERM", shutdown)
await new Promise(() => {})
