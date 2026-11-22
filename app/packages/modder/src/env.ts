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
  /** Root of the server's own data, config, cache and state. Default `<SpecTuner folder>/app` ({@link defaultHome}). */
  readonly home?: string
  /** Absolute path of the sidecar program (`spectuner-sidecar.exe` on Windows, `spectuner-sidecar` elsewhere). */
  readonly sidecar: string
  /** Absolute path of the plugin directory. */
  readonly pluginDir: string
  /** The modder system prompt; defaults to the bundled `src/prompt/modder.txt`. */
  readonly system?: string
  /** Environment to start from. Default `process.env`. */
  readonly base?: Readonly<Record<string, string | undefined>>
  /** The system the server runs on, which picks the default folders. Default `process.platform`. */
  readonly platform?: NodeJS.Platform
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

/**
 * SpecTuner's one folder in the system's usual place for per-user application data. The server's home is `app` (or
 * `app-dev`) inside it, and the sidecar keeps each game's state in `games` inside it (`SPECTUNER_HOME`), so the two
 * always agree:
 * - Windows: `%LOCALAPPDATA%\SpecTuner`
 * - macOS: `~/Library/Application Support/SpecTuner`
 * - Linux and other systems: `${XDG_DATA_HOME:-~/.local/share}/SpecTuner`
 *
 * Read `base` before SpecTuner's own `XDG_*` values are applied: the server's `XDG_DATA_HOME` points into its home.
 * The sidecar falls back to the same folders when it runs without `SPECTUNER_HOME`.
 */
export function folder(
  base: Readonly<Record<string, string | undefined>> = process.env,
  platform: NodeJS.Platform = process.platform,
  home: string = os.homedir(),
) {
  if (platform === "win32") return path.join(base.LOCALAPPDATA ?? path.join(home, "AppData", "Local"), "SpecTuner")
  if (platform === "darwin") return path.join(home, "Library", "Application Support", "SpecTuner")
  // The XDG specification says to ignore a relative value.
  const xdg = base.XDG_DATA_HOME
  return path.join(xdg && path.isAbsolute(xdg) ? xdg : path.join(home, ".local", "share"), "SpecTuner")
}

/** The packaged server's home, `<SpecTuner folder>/app` ({@link folder}). Unpackaged runs use `app-dev` beside it. */
export function defaultHome(
  base: Readonly<Record<string, string | undefined>> = process.env,
  platform: NodeJS.Platform = process.platform,
  home: string = os.homedir(),
) {
  return path.join(folder(base, platform, home), "app")
}

/** The sidecar program's file name: Windows needs the extension, other systems use none. */
export function sidecarFile(platform: NodeJS.Platform = process.platform) {
  return platform === "win32" ? "spectuner-sidecar.exe" : "spectuner-sidecar"
}

/** The .NET runtime identifier the sidecar is published for on this system and processor (`sidecar/publish.sh`). */
export function sidecarRid(platform: NodeJS.Platform = process.platform, arch: string = process.arch) {
  return `${platform === "win32" ? "win" : platform === "darwin" ? "osx" : platform}-${arch}`
}

/**
 * Where `sidecar/publish.sh` writes the sidecar under the repository's `artifacts` folder: `sidecar/spectuner-sidecar.exe`
 * on Windows, `sidecar/<rid>/spectuner-sidecar` on other systems.
 */
export function publishedSidecar(artifacts: string, platform: NodeJS.Platform = process.platform, arch: string = process.arch) {
  if (platform === "win32") return path.join(artifacts, "sidecar", sidecarFile(platform))
  return path.join(artifacts, "sidecar", sidecarRid(platform, arch), sidecarFile(platform))
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
  const platform = input.platform ?? process.platform
  const home = input.home ?? defaultHome(base, platform)
  if (!path.isAbsolute(home)) throw new Error(`home must be absolute: ${home}`)
  if (!path.isAbsolute(input.sidecar)) throw new Error(`sidecar must be absolute: ${input.sidecar}`)
  const layout = dirs(home)
  const inherited = Object.entries(base).filter(
    (entry): entry is [string, string] => entry[1] !== undefined && (OWN.test(entry[0]) || !/^(SPECTUNER_|XDG_)/i.test(entry[0])),
  )
  // The sidecar's state folder is always set, so the sidecar never derives it from the server's XDG folders (on Linux
  // .NET reads XDG_DATA_HOME, which points into the server's home). An inherited value (tests, scripts) is kept as is.
  const sidecarHome: Record<string, string> = inherited.some(([key, value]) => /^SPECTUNER_HOME$/i.test(key) && value !== "")
    ? {}
    : { SPECTUNER_HOME: folder(base, platform) }
  return {
    ...Object.fromEntries(inherited),
    ...sidecarHome,
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
