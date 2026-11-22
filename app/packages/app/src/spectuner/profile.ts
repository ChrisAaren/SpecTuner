import type { TimelineDetail } from "@spectuner/session-ui/timeline/detail"

// SpecTuner. SpecTuner is a desktop app for players who tune or tweak a Unity game they own; a
// locked-down "modder" agent does the work. Every product decision about the shared app UI lives here, and each
// upstream file that needs one consults this module in a short edit, so the diff
// against upstream stays small and obvious. Pure data and functions: the desktop main process imports it too.

/** Palette, keybind, slash-command and menu commands the player never gets. */
const hiddenCommands: ReadonlySet<string> = new Set([
  // Agents: the server's default agent (`modder`) is the only one.
  "agent.cycle",
  "agent.cycle.reverse",
  // Shell mode in the composer runs a command on the player's machine.
  "prompt.mode.toggle",
  "prompt.mode.shell",
  "prompt.mode.normal",
  // The file tree and viewer, review and diffs, the bottom (terminal) pane.
  "file.open",
  "file.close",
  "file.tab.new",
  "file.tab.reopen",
  "file.tab.prev",
  "file.tab.next",
  "context.addSelection",
  "review.toggle",
  "bottom.toggle",
  "terminal.toggle",
  // Folders, branches and worktrees.
  "session.location.cycle",
  "session.location.select",
  "session.branch.choose",
  "project.copyID",
  "session.copyID",
  // Server and session internals, and the Console usage dialog. Undo and redo here rewind the conversation, not the
  // game; the game's own undo is a SpecTuner feature, so these would only mislead.
  "mcp.toggle",
  "config.reload",
  "permissions.autoaccept",
  "usage.view",
  "session.background",
  "session.compact",
  "session.fork",
  "session.export",
  "session.undo",
  "session.redo",
])

/** Settings pages left out of the navigation, search and routes. Kept: Preferences, Appearance, Notifications, Providers, Models, About. */
const hiddenSettingsPages: ReadonlySet<string> = new Set([
  "shortcuts",
  "extensions",
  "projects",
  "workspaces",
  "servers",
  "experimental",
])

/**
 * Rows on the kept settings pages left out, by the `data-action` of their control (the search catalog's `target`):
 * worktrees, permission auto-approval, the agent picker, shells and subagents, diffs, code wrapping, the terminal
 * font, and upstream's help and feedback link.
 */
const hiddenSettingsRows: readonly string[] = [
  "settings-help-feedback",
  "settings-workspace-destination",
  "settings-auto-accept-permissions",
  "settings-show-custom-agents",
  "settings-background-activity",
  "settings-show-turn-diffs",
  "settings-wrap-code-blocks",
  "settings-terminal-font",
]

/** Native and Windows menu actions left out: developer tools. */
const hiddenMenuActions: ReadonlySet<string> = new Set(["view.toggleDevTools"])

const upstreamLinks = false

const serverCommands = false

const fileMentions = false

const skillMentions = false

const voiceCalls = false

export const SpecTunerProfile = {
  /** The product name wherever the app shows one. A proper noun, so it is not translated. */
  name: "SpecTuner",
  /** First launch creates no `Documents/Default Project`; Home asks for the game's folder instead. */
  defaultProject: false,
  /** Home without sessions shows the two first steps (connect a model, choose the game's folder) instead. */
  homeStart: true,
  /** Adding a folder opens a chat on that exact folder, instead of only listing it as a project. */
  chooseGame: true,
  /** A chat runs in the folder it was opened on, never in a git repository that contains it. */
  sessionInOpenedFolder: true,
  /** Upstream's help, feedback and documentation links (opencode.ai, Discord, GitHub) are not shown. */
  upstreamLinks,
  /** Settings > About shows SpecTuner, with OpenCode's attribution and notices, instead of OpenCode's page. */
  ownAbout: true,
  /** A new chat shows SpecTuner's heading and the game folder instead of OpenCode's wordmark (and its hidden games). */
  ownDraftHeading: true,
  /** No project, branch, worktree or location controls on a new chat or in a session's header. */
  locationControls: false,
  /** Default timeline detail over upstream's "compact" preset: each tool call is its own row, so game tool calls read as plain steps. */
  timelineDetail: { tools: { placement: "separate" } } satisfies Partial<TimelineDetail>,
  /** No OpenCode Go, free-limit or ChatGPT-plan panels, dialogs or welcomes. */
  upsells: false,
  /** The session side panel opens only when a plugin lists a tab there (no empty "New tab" launcher). */
  emptySidePanel: false,
  /** The composer has no shell mode: no `!` prefix, no Shell menu item, button or command. */
  shellMode: false,
  /** No agent picker: every session uses the server's default agent. */
  agentPicker: false,
  /** Whether the player can reach the command `id` (palette, keybind, slash or menu). */
  command: (id: string) => !hiddenCommands.has(id),
  /** Whether the settings page `tab` is shown and routable. */
  settingsPage: (tab: string) => !hiddenSettingsPages.has(tab),
  /** Whether the settings row whose control has `data-action` `target` is shown and searchable. */
  settingsRow: (target: string) => !hiddenSettingsRows.includes(target),
  /** The hidden rows' controls, as `data-action` values. */
  hiddenSettingsRows,
  /** Whether a native or Windows menu entry is shown. */
  menuEntry: (entry: { command?: string; action?: string; href?: string }) =>
    (upstreamLinks || !entry.href) &&
    (!entry.command || !hiddenCommands.has(entry.command)) &&
    (!entry.action || !hiddenMenuActions.has(entry.action)),
  /** The server's own slash commands (`/init`, `/review`, `/goal`) are coding prompts: none are offered. */
  serverSlash: <Item>(items: Item[]) => (serverCommands ? items : []),
  /** `@` mentions offer no skills: the modder has no skill tool, and a mention would put a skill's text in the prompt. */
  skillMentions: <Item>(items: Item[]) => (skillMentions ? items : []),
  /** `@` mentions do not search the game's files: the file tree is a coding feature. */
  fileSearch: <Path>(search: () => Promise<Path[]>) => (fileMentions ? search() : Promise.resolve<Path[]>([])),
  /** No voice call button in the composer, whatever upstream's channel and experiment say: SpecTuner has no voice
   * feature, and the server refuses its endpoints under the lock-down. `upstream` is upstream's own decision. */
  voiceCalls: (upstream: boolean) => voiceCalls && upstream,
}
