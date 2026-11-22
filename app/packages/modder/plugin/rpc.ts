export * as SpecTunerRpc from "./rpc.js"

import type { Rpc } from "@spectuner/plugin/rpc"
import { Schema } from "effect"

// The Changes panel's RPC, shared by the server plugin (which serves it) and the desktop's Changes panel (which
// calls it). Values only: `effect` is the one runtime import, so the window bundle pulls in nothing else from here.

/** A mod change's mod name and which build of that mod it is (1 for the first). */
export const ModBuild = Schema.Struct({ name: Schema.String, build: Schema.Number })
export type ModBuild = typeof ModBuild.Type

/** One entry of the sidecar's change ledger (docs/sidecar-contract.md, "Change ledger entry"). */
export const Change = Schema.Struct({
  id: Schema.String,
  kind: Schema.String,
  title: Schema.String,
  description: Schema.String,
  createdAt: Schema.String,
  status: Schema.String,
  /** For a mod: its name and build, else null (also when the helper does not say). */
  mod: Schema.NullOr(ModBuild),
})
export type Change = typeof Change.Type

/** Why a past change is no longer in place (docs/sidecar-contract.md, `changes`, "past"). */
export const PastReason = Schema.Literals(["undone", "restored", "failed_start", "failed_test", "replaced"])
export type PastReason = typeof PastReason.Type

/** A quality or mod change that was once in place and is not any more. */
export const PastChange = Schema.Struct({
  id: Schema.String,
  kind: Schema.String,
  title: Schema.String,
  description: Schema.String,
  createdAt: Schema.String,
  endedAt: Schema.String,
  reason: PastReason,
  /** Whether "Apply again" can put it back. */
  canReapply: Schema.Boolean,
  /** The sidecar's plain sentence saying why it cannot be put back, else null. */
  whyNot: Schema.NullOr(Schema.String),
  /** For a mod: its name and which build of that mod this was (1 for the first), else null. */
  mod: Schema.NullOr(ModBuild),
  /** The change this one was itself put back from, else null. */
  reappliedFrom: Schema.NullOr(Schema.String),
})
export type PastChange = typeof PastChange.Type

/** What the panel shows for the game in the session's folder. */
export const Status = Schema.Struct({
  /** The game's name, or null when the folder holds no game the sidecar recognises. */
  game: Schema.NullOr(Schema.String),
  /** Whether SpecTuner can work with this game. */
  supported: Schema.Boolean,
  /** The sidecar's plain explanation when it cannot (or when it could not be asked), else null. */
  message: Schema.NullOr(Schema.String),
  /** Every change that is still in place, newest first. */
  changes: Schema.Array(Change),
  /** Whether "Restore game" has anything to remove. */
  canRestore: Schema.Boolean,
  /** Changes that were once in place and are not any more, newest first (at most 50). */
  past: Schema.Array(PastChange),
  /** How many past changes there are in all. */
  pastTotal: Schema.Number,
  /** Whether `past` holds only the newest of them. */
  pastTruncated: Schema.Boolean,
})
export type Status = typeof Status.Type

/** The outcome of an undo, a restore or an "Apply again": the sidecar's one-sentence summary, or its plain refusal. */
export const Result = Schema.Struct({ ok: Schema.Boolean, message: Schema.String })
export type Result = typeof Result.Type

export const Definition = {
  id: "spectuner.changes",
  methods: {
    status: {
      input: Schema.toStandardSchemaV1(Schema.Struct({})),
      output: Schema.toStandardSchemaV1(Status),
    },
    undo: {
      input: Schema.toStandardSchemaV1(Schema.Struct({ changeId: Schema.String })),
      output: Schema.toStandardSchemaV1(Result),
    },
    restore: {
      input: Schema.toStandardSchemaV1(Schema.Struct({})),
      output: Schema.toStandardSchemaV1(Result),
    },
    /** "Apply again": puts a past change back as a new change. A mod is rebuilt and the game started once to check it. */
    reapply: {
      input: Schema.toStandardSchemaV1(Schema.Struct({ changeId: Schema.String })),
      output: Schema.toStandardSchemaV1(Result),
    },
  },
  events: {
    /** Published after every game tool call, undo, restore or "Apply again", in the game folder's Location. */
    changed: { schema: Schema.toStandardSchemaV1(Schema.Struct({ directory: Schema.String })) },
  },
} as const satisfies Rpc.PortableDefinition

/** The provider id of the player's local model in the server's config file. */
export const LOCAL_PROVIDER = "spectuner-local"

/** Why "Find models" found nothing usable; the window words each reason for the player. */
export const LocalFindFailure = Schema.Literals([
  "invalid-address",
  "unreachable",
  "rejected",
  "not-compatible",
  "no-models",
])
export type LocalFindFailure = typeof LocalFindFailure.Type

export const LocalModel = Schema.Struct({
  id: Schema.String,
  /** The context window the server reported, else null (SpecTuner then assumes a conservative one). */
  context: Schema.NullOr(Schema.Number),
})

export const LocalFind = Schema.Struct({
  ok: Schema.Boolean,
  reason: Schema.NullOr(LocalFindFailure),
  /** The address as the server will call it. */
  address: Schema.String,
  models: Schema.Array(LocalModel),
})
export type LocalFind = typeof LocalFind.Type

/** The saved local model. */
export const LocalCurrent = Schema.Struct({ address: Schema.String, model: Schema.String, hasKey: Schema.Boolean })
export type LocalCurrent = typeof LocalCurrent.Type

export const LocalResult = Schema.Struct({
  ok: Schema.Boolean,
  /** `invalid-address`, `invalid-model`, or `unreadable` (the config file is not a JSONC object and was left alone). */
  reason: Schema.NullOr(Schema.Literals(["invalid-address", "invalid-model", "unreadable"])),
})
export type LocalResult = typeof LocalResult.Type

/** "Use a local model": find a local server's models, save one as the player's model, or remove it. */
export const LocalDefinition = {
  id: "spectuner.local-model",
  methods: {
    get: {
      input: Schema.toStandardSchemaV1(Schema.Struct({})),
      output: Schema.toStandardSchemaV1(Schema.NullOr(LocalCurrent)),
    },
    find: {
      input: Schema.toStandardSchemaV1(Schema.Struct({ address: Schema.String, key: Schema.optional(Schema.String) })),
      output: Schema.toStandardSchemaV1(LocalFind),
    },
    save: {
      input: Schema.toStandardSchemaV1(
        Schema.Struct({
          address: Schema.String,
          key: Schema.optional(Schema.String),
          model: Schema.String,
          context: Schema.NullOr(Schema.Number),
        }),
      ),
      output: Schema.toStandardSchemaV1(LocalResult),
    },
    remove: {
      input: Schema.toStandardSchemaV1(Schema.Struct({})),
      output: Schema.toStandardSchemaV1(LocalResult),
    },
  },
  events: {},
} as const satisfies Rpc.PortableDefinition
