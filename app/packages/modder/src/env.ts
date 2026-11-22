export * as SpecTunerEnv from "./env.js"

import os from "node:os"
import path from "node:path"
import { SpecTunerConfig } from "./config.js"

// Node builtins only: the desktop main process calls this to launch the server, like `script/serve.ts` does.

/**
 * Every experimental flag, pinned off. Local and dev builds turn flags on by default and release builds turn them
 * off; pinning makes a source run behave like the shipped app. Several add tools or agent behaviour we do not want
 * (`artifacts`, `video_generate`, `machines`, `sqlite_fts`, `todo`, `mcp_events`, `console_watches`, `home_memory`,
 * `full_codemode`, `project_overlay`); the rest change storage or API choice and are kept at their release value.
 */
export const FLAGS = [
  "todo",
  "media_blob",
  "artifacts",
  "video_generate",
  "console_watches",
  "mcp_events",
  "recommended_api",
  "full_codemode",
  "machines",
  "home_memory",
  "project_overlay",
  "sqlite_fts",
] as const

export interface Input {
  /** Root of the server's own data, config, cache and state. Default `%LOCALAPPDATA%\SpecTuner\app`. */
  readonly home?: string
  /** Absolute path of `spectuner-sidecar.exe`. */
  readonly sidecar: string
  /** Absolute path of the plugin directory. */
  readonly pluginDir: string
  /** The modder system prompt; defaults to the bundled `src/prompt/modder.txt`. */
  readonly system?: string
  /** Environment to start from. Default `process.env`. */
  readonly base?: Readonly<Record<string, string | undefined>>
}

/** Directory layout under `home`. */
export function dirs(home: string) {
  return {
    data: path.join(home, "data"),
    config: path.join(home, "config"),
    cache: path.join(home, "cache"),
    state: path.join(home, "state"),
  }
}

export function defaultHome(base: Readonly<Record<string, string | undefined>> = process.env) {
  return path.join(base.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "SpecTuner", "app")
}

/**
 * SpecTuner's own variables, which pass through to the server and from there to the plugin and the sidecar: the
 * sidecar's state folder (`SPECTUNER_HOME`), the sidecar path, and test and build switches. Every other inherited
 * `SPECTUNER_*` variable is one of the server's own settings and is dropped.
 */
export const OWN = /^SPECTUNER_(HOME|SIDECAR|RESOURCES|TEST_GAME)$/i

/**
 * The environment for a SpecTuner server process. Inherited `SPECTUNER_*` server settings (all but {@link OWN}) and
 * `XDG_*` variables are dropped, so a developer's own settings (another config dir, config file, database, flags)
 * cannot reach the server or point it at other data. Add `SPECTUNER_PASSWORD` after calling this.
 */
export function make(input: Input): Record<string, string> {
  const base = input.base ?? process.env
  const home = input.home ?? defaultHome(base)
  if (!path.isAbsolute(home)) throw new Error(`home must be absolute: ${home}`)
  if (!path.isAbsolute(input.sidecar)) throw new Error(`sidecar must be absolute: ${input.sidecar}`)
  const layout = dirs(home)
  const inherited = Object.entries(base).filter(
    (entry): entry is [string, string] => entry[1] !== undefined && (OWN.test(entry[0]) || !/^(SPECTUNER_|XDG_)/i.test(entry[0])),
  )
  return {
    ...Object.fromEntries(inherited),
    XDG_DATA_HOME: layout.data,
    XDG_CONFIG_HOME: layout.config,
    XDG_CACHE_HOME: layout.cache,
    XDG_STATE_HOME: layout.state,
    SPECTUNER_CONFIG_CONTENT: JSON.stringify(
      SpecTunerConfig.document({ pluginDir: input.pluginDir, system: input.system }),
    ),
    SPECTUNER_DISABLE_PROJECT_CONFIG: "1",
    SPECTUNER_DISABLE_CLAUDE_CODE: "1",
    SPECTUNER_DISABLE_AUTOUPDATE: "1",
    // No silent default to OpenCode's free hosted models: with no model connected, the app asks the player to connect one.
    SPECTUNER_DISABLE_FREE_MODELS: "1",
    SPECTUNER_FLAGS: FLAGS.map((flag) => `-${flag}`).join(","),
    // The server refuses every route SpecTuner's app never calls that could run a command, touch files, add a tool
    // server or plugin, open remote access or call the network (packages/server/src/middleware/lockdown.ts).
    SPECTUNER_LOCKDOWN: "1",
    SPECTUNER_SIDECAR: input.sidecar,
  }
}
