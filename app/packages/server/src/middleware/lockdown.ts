export * as ServerLockdown from "./lockdown"

import { MACHINE_LINK_PATH } from "@spectuner/protocol/groups/machine"
import { MCP_CLIENT_PATH } from "@spectuner/protocol/groups/mcp"
import { PERSISTENT_PTY_CONNECT_PATH } from "@spectuner/protocol/groups/persistent-pty"
import { PTY_CONNECT_PATH } from "@spectuner/protocol/groups/pty"
import { PAIRING_CONNECT_PATH } from "@spectuner/protocol/groups/server"
import { HttpServerResponse } from "effect/http"

/**
 * SpecTuner's lock-down. With this variable set to `1` (SpecTuner's server environment, `packages/modder/src/env.ts`,
 * always sets it) the server refuses every route family below with 403, before credentials are even checked: running
 * commands, terminals, writing files and reading arbitrary ones, adding tool servers, plugins or integration commands,
 * remote access, and network features other than model calls. SpecTuner's app never calls these; the modder agent
 * cannot reach them either way (it only has the game tools). Without the variable nothing changes.
 */
export const ENV = "SPECTUNER_LOCKDOWN"

export function enabled(env: Readonly<Record<string, string | undefined>> = process.env) {
  const value = env[ENV]?.trim().toLowerCase()
  return value === "1" || value === "true"
}

export type Family =
  | "shell"
  | "terminal"
  | "command"
  | "file-write"
  | "file-read"
  | "tool-server"
  | "plugin"
  | "config"
  | "remote-access"
  | "network"
  | "automation"

type Rule = { readonly method: string | "*"; readonly path: string; readonly family: Family }

const any = (family: Family, ...paths: string[]): Rule[] => paths.map((path) => ({ method: "*", path, family }))

/** Refused routes, by the route template the router selected (never by the raw URL) and method (`*` for all). */
export const RULES: readonly Rule[] = [
  // Commands on the player's machine.
  { method: "POST", path: "/api/session/:sessionID/shell", family: "shell" },
  { method: "PUT", path: "/api/session/:sessionID/environment", family: "shell" },
  // Only starting one: the app's location sync lists running shells (`GET /api/shell`, always empty here) and fails
  // as a whole if that list is refused.
  { method: "POST", path: "/api/shell", family: "shell" },
  // A slash command's template runs `!`...`` shell snippets, and the player's arguments are substituted first.
  { method: "POST", path: "/api/session/:sessionID/command", family: "command" },
  { method: "POST", path: "/api/experimental/session/:sessionID/skill", family: "command" },
  ...any(
    "terminal",
    "/api/pty",
    "/api/pty/:ptyID",
    "/api/pty/:ptyID/connect-token",
    PTY_CONNECT_PATH,
    "/api/experimental/session/:sessionID/terminal",
    "/api/experimental/session/:sessionID/terminal/read",
    "/api/experimental/persistent-pty/shutdown",
    "/api/experimental/persistent-pty/handoff",
    "/api/experimental/persistent-pty/:ptyID",
    "/api/experimental/persistent-pty/:ptyID/snapshot",
    "/api/experimental/persistent-pty/:ptyID/connect-token",
    PERSISTENT_PTY_CONNECT_PATH,
  ),
  ...any(
    "automation",
    "/api/experimental/automation",
    "/api/experimental/automation/:automationID",
    "/api/experimental/automation/:automationID/pause",
    "/api/experimental/automation/:automationID/resume",
  ),
  // Files: writes, watches, git init, worktrees (git plus the project's start script, and directory removal), heap
  // dumps written to a given folder, artifacts.
  { method: "POST", path: "/api/experimental/fs/write", family: "file-write" },
  ...any("file-read", "/api/experimental/fs/watch"),
  { method: "POST", path: "/api/vcs/init", family: "file-write" },
  { method: "POST", path: "/api/worktree", family: "file-write" },
  { method: "DELETE", path: "/api/worktree", family: "file-write" },
  { method: "POST", path: "/api/debug/heap-dump", family: "file-write" },
  ...any(
    "file-write",
    "/api/experimental/artifacts",
    "/api/experimental/artifacts/providers",
    "/api/experimental/artifacts/:artifactID",
    "/api/experimental/artifacts/:artifactID/view/*",
  ),
  // Tool servers (an MCP server can be a local command), plugins (npm), and integrations whose login is a command.
  ...any(
    "tool-server",
    "/api/experimental/mcp/:server",
    "/api/experimental/mcp/:server/connect",
    "/api/experimental/mcp/:server/disconnect",
    MCP_CLIENT_PATH,
    `${MCP_CLIENT_PATH}/token`,
    "/api/experimental/integration/wellknown",
    "/api/integration/:integrationID/connect/command",
    "/api/integration/:integrationID/connect/command/:attemptID",
  ),
  { method: "POST", path: "/api/plugin/check", family: "plugin" },
  { method: "POST", path: "/api/plugin/update", family: "plugin" },
  // Its only field is the shell that runs commands.
  { method: "PATCH", path: "/api/experimental/config", family: "config" },
  // Other computers: machines, their link, pairing.
  ...any(
    "remote-access",
    "/api/experimental/machine",
    "/api/experimental/machine/:name",
    MACHINE_LINK_PATH,
    "/api/pair",
    PAIRING_CONNECT_PATH,
  ),
  // Network features that are not the player's model: web search, media generation, voice.
  ...any(
    "network",
    "/api/websearch",
    "/api/websearch/provider",
    "/api/experimental/generate",
    "/api/experimental/generate/text",
    "/api/experimental/generate/image",
    "/api/experimental/generate/video",
    "/api/experimental/generate/video/:id",
    "/api/experimental/generate/decisions",
    "/api/experimental/voice/models",
    "/api/experimental/voice/session",
  ),
]

const FILE_READ_PATH = "/api/fs/read/*"
const FILE_READ_PREFIX = "/api/fs/read/"

/**
 * The app reads local files only to show images (pictures in a chat message, image attachments). In lock-down the
 * file route serves those image types and nothing else.
 */
export const IMAGE_EXTENSIONS: ReadonlySet<string> = new Set([
  "png",
  "jpeg",
  "jpg",
  "gif",
  "webp",
  "svg",
  "avif",
  "bmp",
  "ico",
])

function imagePath(pathname: string) {
  if (!pathname.startsWith(FILE_READ_PREFIX)) return false
  const decoded = (() => {
    try {
      return decodeURIComponent(pathname.slice(FILE_READ_PREFIX.length))
    } catch {
      return undefined
    }
  })()
  // `:` would name an NTFS alternate stream of some other file.
  if (decoded === undefined || decoded.includes("\0") || decoded.includes(":")) return false
  const extension = /\.([^./\\]+)$/.exec(decoded)?.[1]?.toLowerCase()
  return extension !== undefined && IMAGE_EXTENSIONS.has(extension)
}

/** The family a request belongs to when lock-down refuses it, else undefined. */
export function refused(method: string, route: string, url: URL): Family | undefined {
  const upper = method.toUpperCase()
  if (route === FILE_READ_PATH) return imagePath(url.pathname) ? undefined : "file-read"
  return RULES.find((rule) => rule.path === route && (rule.method === "*" || rule.method === upper))?.family
}

const MESSAGES: Record<Family, string> = {
  shell: "running commands",
  terminal: "terminals",
  command: "commands and skills",
  "file-write": "changing files",
  "file-read": "reading this file",
  "tool-server": "adding tool servers or login commands",
  plugin: "installing or updating plugins",
  config: "changing the server's settings",
  "remote-access": "remote access",
  network: "web search, generation and voice",
  automation: "automations",
}

/** 403 in the shape the HttpApi encodes `ForbiddenError`, so clients decode it like any refused request. */
export function response(family: Family) {
  return HttpServerResponse.jsonUnsafe(
    {
      _tag: "ForbiddenError",
      message: `SpecTuner does not allow ${MESSAGES[family]} (${family}).`,
    },
    { status: 403 },
  )
}
