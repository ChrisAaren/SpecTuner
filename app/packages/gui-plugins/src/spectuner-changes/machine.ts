import type { SpecTunerRpc } from "@spectuner/modder/rpc"

// The Changes panel's lifecycle for one session's game: reading the game's status, and one undo, restore or
// "Apply again" at a time. Reads and actions carry attempt ids so a late answer never overwrites a newer one.

export type ChangesLoad =
  | { readonly type: "loading"; readonly attempt: number; readonly failures: number }
  | { readonly type: "ready"; readonly status: SpecTunerRpc.Status; readonly attempt: number }
  | { readonly type: "failed"; readonly message: string; readonly attempt: number; readonly failures: number }

export type ChangesAction =
  | { readonly type: "idle" }
  | { readonly type: "undoing"; readonly attempt: number; readonly changeId: string }
  | { readonly type: "restoring"; readonly attempt: number }
  | { readonly type: "reapplying"; readonly attempt: number; readonly changeId: string }

/** The last action's outcome: the sidecar's sentence, or its plain refusal. */
export type ChangesNotice = { readonly ok: boolean; readonly message: string }

export interface ChangesState {
  readonly load: ChangesLoad
  readonly action: ChangesAction
  readonly notice: ChangesNotice | undefined
  /** The last attempt id handed out, for reads and actions alike. */
  readonly attempts: number
  /**
   * Whether "Past changes" is expanded. Undefined until the first status arrives, which opens it only when nothing is
   * changed now; later reads leave it alone, so the list does not jump while the player works with it.
   */
  readonly pastOpen: boolean | undefined
}

export type ChangesEvent =
  | { readonly type: "refresh" }
  | { readonly type: "retry.due"; readonly attempt: number }
  | { readonly type: "loaded"; readonly attempt: number; readonly status: SpecTunerRpc.Status }
  | { readonly type: "load.failed"; readonly attempt: number; readonly message: string }
  | { readonly type: "undo"; readonly changeId: string }
  | { readonly type: "restore" }
  | { readonly type: "reapply"; readonly changeId: string }
  | { readonly type: "past.toggle" }
  | { readonly type: "action.done"; readonly attempt: number; readonly result: SpecTunerRpc.Result }
  | { readonly type: "action.failed"; readonly attempt: number; readonly message: string }
  | { readonly type: "notice.dismiss" }

export type ChangesEffect =
  | { readonly type: "fetch"; readonly attempt: number }
  | { readonly type: "retry"; readonly attempt: number; readonly delay: number }
  | { readonly type: "undo"; readonly attempt: number; readonly changeId: string }
  | { readonly type: "restore"; readonly attempt: number }
  | { readonly type: "reapply"; readonly attempt: number; readonly changeId: string }

export interface ChangesTransition {
  readonly state: ChangesState
  readonly effects: readonly ChangesEffect[]
}

const NONE: readonly ChangesEffect[] = []

/** The server answers "not found" until the plugin has loaded, so a first read is retried while it fails. */
export const RETRY_DELAY_MS = 3000

/** Failed reads shown as still loading before the message appears (about the plugin's load time). */
const QUIET_FAILURES = 3

/** The state a panel starts in; it asks for the status at once. */
export function createChangesState(): ChangesTransition {
  return {
    state: {
      load: { type: "loading", attempt: 1, failures: 0 },
      action: { type: "idle" },
      notice: undefined,
      attempts: 1,
      pastOpen: undefined,
    },
    effects: [{ type: "fetch", attempt: 1 }],
  }
}

export function transitionChanges(state: ChangesState, event: ChangesEvent): ChangesTransition {
  if (event.type === "refresh") return fetch(state)

  // A retry is due only while its failed read is still the latest one.
  if (event.type === "retry.due") {
    if (state.load.type === "ready" || state.load.attempt !== event.attempt) return { state, effects: NONE }

    return fetch(state)
  }

  if (event.type === "loaded") {
    if (event.attempt !== state.load.attempt) return { state, effects: NONE }

    const pastOpen = state.pastOpen ?? !event.status.changes.some((change) => !isSetup(change.kind))

    return {
      state: { ...state, load: { type: "ready", status: event.status, attempt: event.attempt }, pastOpen },
      effects: NONE,
    }
  }

  if (event.type === "load.failed") {
    if (event.attempt !== state.load.attempt) return { state, effects: NONE }

    // A failed refresh keeps the list the player already sees.
    if (state.load.type === "ready") return { state, effects: NONE }

    const failures = state.load.failures + 1

    const load: ChangesLoad =
      failures < QUIET_FAILURES
        ? { type: "loading", attempt: event.attempt, failures }
        : { type: "failed", message: event.message, attempt: event.attempt, failures }

    return {
      state: { ...state, load },
      effects: [{ type: "retry", attempt: event.attempt, delay: RETRY_DELAY_MS }],
    }
  }

  if (event.type === "past.toggle") return { state: { ...state, pastOpen: !state.pastOpen }, effects: NONE }

  if (event.type === "undo" || event.type === "restore" || event.type === "reapply") {
    // One action at a time: the sidecar would refuse a second one as busy anyway.
    if (state.action.type !== "idle") return { state, effects: NONE }

    const attempt = state.attempts + 1

    const action: ChangesAction =
      event.type === "undo"
        ? { type: "undoing", attempt, changeId: event.changeId }
        : event.type === "reapply"
          ? { type: "reapplying", attempt, changeId: event.changeId }
          : { type: "restoring", attempt }

    const effect: ChangesEffect =
      event.type === "undo"
        ? { type: "undo", attempt, changeId: event.changeId }
        : event.type === "reapply"
          ? { type: "reapply", attempt, changeId: event.changeId }
          : { type: "restore", attempt }

    return { state: { ...state, action, notice: undefined, attempts: attempt }, effects: [effect] }
  }

  if (event.type === "action.done" || event.type === "action.failed") {
    if (state.action.type === "idle" || state.action.attempt !== event.attempt) return { state, effects: NONE }

    const notice = event.type === "action.done" ? event.result : { ok: false, message: event.message }

    return fetch({ ...state, action: { type: "idle" }, notice })
  }

  if (!state.notice) return { state, effects: NONE }

  return { state: { ...state, notice: undefined }, effects: NONE }
}

/** A new read; the list on screen stays until its answer arrives. */
function fetch(state: ChangesState): ChangesTransition {
  const attempt = state.attempts + 1
  const load: ChangesLoad = state.load.type === "ready" ? { ...state.load, attempt } : { ...state.load, attempt }

  return { state: { ...state, load, attempts: attempt }, effects: [{ type: "fetch", attempt }] }
}

/** Setup entries: the mod loader and SpecTuner's probe, shown as one quiet line without Undo. */
const isSetup = (kind: string) => kind === "loader" || kind === "probe"

/** Which build of its mod a change is, when the current and past lists together hold more than one build of it. */
interface Versioned {
  readonly version: number | undefined
}

/** A current change as the panel shows it. */
export interface ChangeView extends SpecTunerRpc.Change, Versioned {}

/** A past change as the panel shows it. */
export interface PastView extends SpecTunerRpc.PastChange, Versioned {}

export type ChangesView =
  | { readonly phase: "loading" }
  | { readonly phase: "failed"; readonly message: string }
  | {
      readonly phase: "ready"
      readonly game: string | null
      /** The sidecar's plain reason when SpecTuner cannot work with this game, else undefined. */
      readonly declined: string | undefined
      /** The player's changes, newest first. */
      readonly changes: readonly ChangeView[]
      /** Whether SpecTuner's own setup is in the game. */
      readonly setup: boolean
      readonly canRestore: boolean
      readonly undoing: string | undefined
      readonly restoring: boolean
      /** Changes that were once in place and are not any more, newest first. */
      readonly past: readonly PastView[]
      /** How many past changes there are in all, and whether `past` holds only the newest of them. */
      readonly pastTotal: number
      readonly pastTruncated: boolean
      /** Whether the past list is expanded. */
      readonly pastOpen: boolean
      /** The past change being put back, else undefined. */
      readonly reapplying: string | undefined
      readonly busy: boolean
      readonly notice: ChangesNotice | undefined
    }

/** What the panel shows. */
export function selectChangesView(state: ChangesState): ChangesView {
  if (state.load.type === "loading") return { phase: "loading" }

  if (state.load.type === "failed") return { phase: "failed", message: state.load.message }

  const status = state.load.status
  const current = status.changes.filter((change) => !isSetup(change.kind))

  // The builds of each mod across both lists: a version number is only worth showing when there are several, such as
  // one build in place and another under Past changes.
  const builds = [...current, ...status.past].reduce((found, item) => {
    if (item.mod) found.set(item.mod.name, (found.get(item.mod.name) ?? new Set<number>()).add(item.mod.build))
    return found
  }, new Map<string, Set<number>>())

  const version = (item: { readonly mod: SpecTunerRpc.ModBuild | null }) =>
    item.mod && (builds.get(item.mod.name)?.size ?? 0) > 1 ? item.mod.build : undefined

  return {
    phase: "ready",
    game: status.game,
    declined: status.supported ? undefined : (status.message ?? undefined),
    changes: current.map((change) => ({ ...change, version: version(change) })),
    setup: status.changes.some((change) => isSetup(change.kind)),
    canRestore: status.canRestore,
    undoing: state.action.type === "undoing" ? state.action.changeId : undefined,
    restoring: state.action.type === "restoring",
    past: status.past.map((item) => ({ ...item, version: version(item) })),
    pastTotal: status.pastTotal,
    pastTruncated: status.pastTruncated,
    pastOpen: state.pastOpen ?? false,
    reapplying: state.action.type === "reapplying" ? state.action.changeId : undefined,
    busy: state.action.type !== "idle",
    notice: state.notice,
  }
}
