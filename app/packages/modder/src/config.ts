export * as SpecTunerConfig from "./config.js"

import { readFileSync } from "node:fs"
import path from "node:path"
import { fileURLToPath } from "node:url"

// Node builtins only: the desktop main process imports this module too.

export const MODDER = "modder"

/** Effective names of the plugin's tools. Keep in sync with `plugin/tools.ts` (a test checks it). */
export const TOOLS = [
  "game_detect",
  "game_benchmark",
  "game_set_quality",
  "game_changes",
  "game_revert",
  "game_search_code",
  "game_read_code",
  "game_build_mod",
  "game_read_logs",
  "game_reapply",
] as const

/** Built-in selectable agents the player must never reach. `title`, `compaction` and `summary` stay: the server uses them. */
export const DISABLED_AGENTS = ["build", "plan", "general", "explore"] as const

/**
 * Built-in plugins removed so their tools are not in the registry at all. Order matters: the input repair hook
 * matches `spectuner.tool.*`, so it is added back afterwards; it fixes malformed arguments from small models.
 * `spectuner.config.mcp` turns the `mcp` section of any config file into MCP servers, which start (a local one is a
 * command on the player's machine) as soon as a folder is opened; without it no configured tool server is loaded.
 */
export const REMOVED_PLUGINS = [
  "-spectuner.tool.*",
  "-spectuner.tools",
  "-spectuner.tools.*",
  "-spectuner.browser",
  "-spectuner.config.mcp",
  "-spectuner.mcp.codemode.defaults",
  "spectuner.tool.input.repair",
] as const

export const PROMPT_FILE = fileURLToPath(new URL("./prompt/modder.txt", import.meta.url))

export const DENY_ALL = { action: "*", resource: "*", effect: "deny" } as const

export interface Input {
  /** Absolute path of the plugin directory (`packages/modder/plugin`, or its copy in the app's resources). */
  readonly pluginDir: string
  /** The modder system prompt. Defaults to `src/prompt/modder.txt`. */
  readonly system?: string
}

/**
 * The server config document for a SpecTuner server, passed as `SPECTUNER_CONFIG_CONTENT`. It outranks every config
 * file, so a stray file cannot undo the lock-down, while the player's provider settings in the writable config dir
 * still load. Nothing here names a provider or a model.
 */
export function document(input: Input) {
  if (!path.isAbsolute(input.pluginDir)) throw new Error(`pluginDir must be absolute: ${input.pluginDir}`)
  return {
    default_agent: MODDER,
    // Pushed onto every agent before its own rules, so the hidden title, compaction and summary agents get no tools.
    permissions: [DENY_ALL],
    // No skills at all: the server would otherwise list skills from the player's home folder (~/.agents/skills,
    // other agents' folders) and offer them to the model.
    policies: [{ action: "skill.use", resource: "*", effect: "deny" } as const],
    agents: {
      [MODDER]: {
        mode: "primary" as const,
        description: "Changes this game for you: smoother performance or gameplay tweaks, each one undoable.",
        system: input.system ?? readFileSync(PROMPT_FILE, "utf8"),
        // A performance pass is up to 9 benchmarks, 4 changes and 4 undos; a mod pass is searches, reads, up to 4
        // builds, a test run and logs. Leave headroom for retries and the final summary.
        steps: 60,
        permissions: [DENY_ALL, ...TOOLS.map((action) => ({ action, resource: "*", effect: "allow" as const }))],
      },
      ...Object.fromEntries(DISABLED_AGENTS.map((id) => [id, { disabled: true }])),
    },
    plugins: [input.pluginDir, ...REMOVED_PLUGINS],
    // The game folder is not a code project: no file snapshots of it, no sharing, no timers, no self-update.
    snapshots: false,
    share: "disabled" as const,
    automations: false,
    update: "disable" as const,
  }
}
