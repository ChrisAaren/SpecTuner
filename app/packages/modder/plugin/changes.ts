export * as SpecTunerChanges from "./changes.ts"

import { Option, Schema } from "effect"
import type { SpecTunerRpc } from "./rpc.ts"
import { Sidecar } from "./sidecar.ts"

// The Changes panel's handlers: the sidecar's `detect`, `changes`, `revert`, `reapply` and `play`, called directly (no
// model involved) for the game in the session's folder.

const TIMEOUT_MS = 2 * Sidecar.MINUTE

/** Putting a mod back rebuilds it and starts the game once to check it, like `game_build_mod`. */
const REAPPLY_TIMEOUT_MS = 10 * Sidecar.MINUTE

/** "Play" returns once the game has started; it never waits for the game to exit. */
const PLAY_TIMEOUT_MS = Sidecar.MINUTE

const PlayData = Schema.Struct({
  steamLaunchOption: Schema.optional(Schema.NullOr(Schema.String)),
  note: Schema.optional(Schema.NullOr(Schema.String)),
})

const decodePlay = Schema.decodeUnknownOption(PlayData)

const DetectData = Schema.Struct({
  supported: Schema.optional(Schema.Boolean),
  game: Schema.optional(Schema.NullOr(Schema.Struct({ name: Schema.optional(Schema.NullOr(Schema.String)) }))),
})

const Entry = Schema.Struct({
  id: Schema.String,
  kind: Schema.String,
  title: Schema.optional(Schema.NullOr(Schema.String)),
  description: Schema.optional(Schema.NullOr(Schema.String)),
  createdAt: Schema.optional(Schema.NullOr(Schema.String)),
  status: Schema.String,
})

const ChangesData = Schema.Struct({
  changes: Schema.Array(Entry),
  canRestore: Schema.optional(Schema.Boolean),
  /** Mod name and build per current mod change, by change id. An older helper does not send it. */
  mods: Schema.optional(Schema.Record(Schema.String, Schema.Unknown)),
  past: Schema.optional(Schema.Array(Schema.Unknown)),
  pastTotal: Schema.optional(Schema.Number),
  pastTruncated: Schema.optional(Schema.Boolean),
})

const Mod = Schema.Struct({ name: Schema.String, build: Schema.Number })

const decodeMod = Schema.decodeUnknownOption(Mod)

const Optional = Schema.optional(Schema.NullOr(Schema.String))

const PastEntry = Schema.Struct({
  id: Schema.String,
  kind: Schema.String,
  title: Optional,
  description: Optional,
  createdAt: Optional,
  endedAt: Optional,
  reason: Schema.String,
  canReapply: Schema.optional(Schema.Boolean),
  whyNot: Optional,
  mod: Schema.optional(Schema.NullOr(Mod)),
  reappliedFrom: Optional,
})

const decodePast = Schema.decodeUnknownOption(PastEntry)

/** Reasons the panel words; any other one (from a newer helper) reads as undone. */
const REASONS: readonly string[] = ["undone", "restored", "failed_start", "failed_test", "replaced"]

const decodeDetect = Schema.decodeUnknownOption(DetectData)

const decodeChanges = Schema.decodeUnknownOption(ChangesData)

/** Ledger states that are still in the game. `pending` is a change the sidecar began; it counts as live. */
const LIVE = new Set(["active", "pending"])

export interface Options {
  readonly game: string
  readonly exe: () => string | undefined
  readonly signal?: AbortSignal
}

/** The game's name, whether SpecTuner can work with it, and the changes still in place, newest first. */
export async function status(options: Options): Promise<SpecTunerRpc.Status> {
  const detect = await call(options, "detect", {})

  if (detect instanceof Error)
    return { game: null, supported: false, message: detect.message, changes: [], canRestore: false, ...NO_PAST }

  const facts = Option.getOrUndefined(decodeDetect(detect.data))
  const supported = detect.ok && facts?.supported !== false
  const listed = await call(options, "changes", {})
  const ledger = listed instanceof Error || !listed.ok ? undefined : Option.getOrUndefined(decodeChanges(listed.data))
  // A mod build that a newer build replaced is still "active" in the sidecar's ledger (undoing the newer build brings
  // it back), and the sidecar also lists it under past changes. It is not what is in place, so it shows only there.
  const superseded = new Set(
    (ledger?.past ?? []).flatMap((item) => {
      const entry = Option.getOrUndefined(decodePast(item))
      return entry ? [entry.id] : []
    }),
  )
  const changes = (ledger?.changes ?? [])
    .filter((entry) => LIVE.has(entry.status) && !superseded.has(entry.id))
    .map((entry) => ({
      id: entry.id,
      kind: entry.kind,
      title: entry.title ?? entry.id,
      description: entry.description ?? "",
      createdAt: entry.createdAt ?? "",
      status: entry.status,
      // An unreadable entry, or none at all, only leaves the version label off.
      mod: Option.getOrNull(decodeMod(ledger?.mods?.[entry.id])),
    }))
  // One unreadable past entry is left out rather than hiding the whole list.
  const past = (ledger?.past ?? []).flatMap((item) => {
    const entry = Option.getOrUndefined(decodePast(item))
    if (!entry) return []
    const reason = (REASONS.includes(entry.reason) ? entry.reason : "undone") as SpecTunerRpc.PastReason
    return [
      {
        id: entry.id,
        kind: entry.kind,
        title: entry.title ?? entry.id,
        description: entry.description ?? "",
        createdAt: entry.createdAt ?? "",
        endedAt: entry.endedAt ?? "",
        reason,
        canReapply: entry.canReapply === true,
        whyNot: entry.canReapply === true ? null : (entry.whyNot ?? null),
        mod: entry.mod ?? null,
        reappliedFrom: entry.reappliedFrom ?? null,
      },
    ]
  })

  return {
    game: facts?.game?.name ?? null,
    supported,
    message: supported ? null : (detect.error?.message ?? detect.summary ?? null),
    changes,
    canRestore: ledger?.canRestore ?? changes.length > 0,
    past,
    pastTotal: ledger?.pastTotal ?? past.length,
    pastTruncated: ledger?.pastTruncated ?? false,
  }
}

const NO_PAST = { past: [], pastTotal: 0, pastTruncated: false } as const

/** Undoes one change. */
export function undo(options: Options, changeId: string) {
  return result(call(options, "revert", { changeId }))
}

/** Removes everything SpecTuner added ("Restore game"). */
export function restore(options: Options) {
  return result(call(options, "revert", { all: true }))
}

/** "Apply again": puts a past change back as a new change. For a mod this starts the game once to check it. */
export function reapply(options: Options, changeId: string) {
  return result(call(options, "reapply", { changeId }, REAPPLY_TIMEOUT_MS))
}

/**
 * "Play": starts the game for the player, with or without SpecTuner's changes. The sidecar returns as soon as the game
 * has started, so the short timeout applies.
 */
export async function play(options: Options, withChanges: boolean): Promise<SpecTunerRpc.PlayResult> {
  const pending = call(options, "play", { withChanges }, PLAY_TIMEOUT_MS)
  const outcome = await result(pending)
  const envelope = await pending
  const data = envelope instanceof Error || !outcome.ok ? undefined : Option.getOrUndefined(decodePlay(envelope.data))

  return { ...outcome, steamLaunchOption: data?.steamLaunchOption ?? null, note: data?.note ?? null }
}

/** One sidecar call; a helper that cannot run comes back as an Error with a plain message instead of throwing. */
function call(options: Options, command: string, request: Readonly<Record<string, unknown>>, timeoutMs = TIMEOUT_MS) {
  // `run` throws at once when the helper is missing; that is a plain message for the panel too.
  return Promise.try(() =>
    Sidecar.run({
      exe: options.exe(),
      command,
      request: { ...request, gamePath: options.game },
      timeoutMs,
      signal: options.signal,
    }),
  ).catch((error: Error) => error)
}

async function result(pending: ReturnType<typeof call>): Promise<SpecTunerRpc.Result> {
  const envelope = await pending

  if (envelope instanceof Error) return { ok: false, message: envelope.message }

  if (!envelope.ok) return { ok: false, message: envelope.error?.message ?? envelope.summary ?? envelope.command }

  return { ok: true, message: envelope.summary ?? envelope.command }
}
