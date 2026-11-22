export * as SpecTunerTools from "./tools.ts"

import type { Info, ToolContext } from "@spectuner/plugin/promise/tool"
import { Schema } from "effect"
import { Sidecar } from "./sidecar.ts"

/** The effective names of every tool this plugin adds, in registration order. */
export const names = [
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

const QUALITY_KEYS = [
  "qualityLevel",
  "vSyncCount",
  "targetFrameRate",
  "resolutionWidth",
  "resolutionHeight",
  "fullscreenMode",
  "renderScale",
  "shadows",
  "shadowDistance",
  "shadowResolution",
  "shadowCascades",
  "antiAliasing",
  "anisotropicFiltering",
  "textureMipmapLimit",
  "lodBias",
  "maximumLODLevel",
  "pixelLightCount",
  "softParticles",
  "realtimeReflectionProbes",
]

const NoInput = Schema.Struct({})

/** Tools only read the abort signal from their context. */
type Context = Pick<ToolContext, "signal">

/** Quality keys a trial refuses (docs/sidecar-contract.md): they need game_set_quality plus a normal benchmark. */
const TRIAL_REFUSED = [
  "qualityLevel",
  "resolutionWidth",
  "resolutionHeight",
  "fullscreenMode",
  "vSyncCount",
  "targetFrameRate",
]
export const TRIAL_KEYS = QUALITY_KEYS.filter((key) => !TRIAL_REFUSED.includes(key))

const Between = (minimum: number, maximum: number) =>
  Schema.Finite.check(Schema.isGreaterThanOrEqualTo(minimum), Schema.isLessThanOrEqualTo(maximum))

const TrySettings = Schema.Record(Schema.String, Schema.Unknown).check(
  Schema.makeFilter(
    (settings) => Object.keys(settings).length > 0 && Object.keys(settings).every((key) => TRIAL_KEYS.includes(key)),
    { expected: `at least one of: ${TRIAL_KEYS.join(", ")}` },
  ),
)

// The measurement design (warm-up, rounds, window length, settle pause) is the sidecar's job: its defaults always
// apply. Inputs not listed here are dropped when the input is decoded, so they never reach the sidecar.
export const BenchmarkInput = Schema.Struct({
  label: Schema.String.annotate({
    description: 'Short name for this run, such as "before", "after", "try: shorter shadows" or "test jump mod".',
  }),
  measureSeconds: Schema.optionalKey(Between(1, 300)).annotate({
    description:
      "Use 5 when testing a mod: a shorter run that is enough to see it load. Leave out otherwise. Not used with trySettings.",
  }),
  trySettings: Schema.optionalKey(TrySettings).annotate({
    description: `Settings to try without saving them, for example {"shadowDistance": 20}. The run then compares the game with and without them. Allowed keys: ${TRIAL_KEYS.join(", ")}. Leave out to measure the game as it is now.`,
  }),
})

const QualityInput = Schema.Struct({
  title: Schema.String.annotate({ description: 'Short plain name for the change, such as "Lower shadow distance".' }),
  description: Schema.String.annotate({ description: "One plain sentence saying what the change does." }),
  settings: Schema.Record(Schema.String, Schema.Unknown).annotate({
    description: `Settings to change, for example {"shadowDistance": 40}. Use null to remove an earlier override. Allowed keys: ${QUALITY_KEYS.join(", ")}.`,
  }),
})

const RevertInput = Schema.Struct({
  changeId: Schema.optionalKey(Schema.String).annotate({
    description: 'Id of the one change to undo, such as "c0003", from game_changes or from the tool that made it.',
  }),
  all: Schema.optionalKey(Schema.Boolean).annotate({
    description: "true to undo everything and restore the game to how it was first found.",
  }),
})

const ReapplyInput = Schema.Struct({
  changeId: Schema.String.annotate({
    description: 'Id of the past change to put back, such as "c0004", from the past list of game_changes.',
  }),
})

const IntBetween = (minimum: number, maximum: number) => Schema.Int.check(Schema.isBetween({ minimum, maximum }))

const Assembly = Schema.optionalKey(Schema.String).annotate({
  description:
    'Only to look inside one other assembly, such as "UnityEngine.CoreModule". Leave out to use the game\'s own code.',
})

export const SearchInput = Schema.Struct({
  query: Schema.String.check(Schema.isMinLength(2)).annotate({
    description: 'A word or name to look for (2 characters or more), such as "jump" or "PlayerController".',
  }),
  kind: Schema.optionalKey(Schema.Literals(["any", "name", "text"])).annotate({
    description:
      '"name" finds types, fields and methods by name; "text" finds lines of code that use the word; "any" (default) does both.',
  }),
  limit: Schema.optionalKey(IntBetween(1, 50)).annotate({ description: "Most hits to return, 1 to 50. Default 20." }),
  assembly: Assembly,
})

export const ReadInput = Schema.Struct({
  type: Schema.String.annotate({
    description: 'Type name from a search hit, such as "SampleGame.PlayerController".',
  }),
  member: Schema.optionalKey(Schema.String).annotate({
    description:
      'Method, property or field name, such as "Update" ("ctor" for constructors). Leave out for the whole type.',
  }),
  startLine: Schema.optionalKey(IntBetween(1, 1_000_000)).annotate({
    description:
      "Without member: read the type from this line on. Use the line numbers from an outline or a search hit.",
  }),
  maxLines: Schema.optionalKey(IntBetween(10, 400)).annotate({
    description: "Most lines to return, 10 to 400. Default 200.",
  }),
  assembly: Assembly,
})

export const BuildInput = Schema.Struct({
  name: Schema.String.check(
    Schema.isPattern(/^[A-Za-z][A-Za-z0-9]{0,39}$/u),
    Schema.makeFilter((name) => name.toLowerCase() !== "spectuner", { expected: "a name other than SpecTuner" }),
  ).annotate({
    description: 'Mod file name: letters and digits only, starting with a letter, at most 40, such as "DoubleJump".',
  }),
  title: Schema.String.annotate({ description: 'Short plain name for the change, such as "Double the jump height".' }),
  description: Schema.String.annotate({ description: "One plain sentence saying what the mod does." }),
  source: Schema.String.annotate({ description: "The complete C# 9 source of the BepInEx 5 plugin, in one file." }),
})

export const LogsInput = Schema.Struct({
  source: Schema.optionalKey(Schema.Literals(["bepinex", "player"])).annotate({
    description: '"bepinex" (default): the mod loader\'s log, where mods write. "player": the game\'s own log.',
  }),
  filter: Schema.optionalKey(Schema.String).annotate({
    description: 'Only lines containing this text, such as your mod\'s display name ("Double Jump").',
  }),
  tail: Schema.optionalKey(IntBetween(1, 200)).annotate({ description: "Most lines to return, 1 to 200. Default 60." }),
})

/**
 * The SpecTuner game tools for one game folder. The model never supplies a path: every request carries `game`.
 * `exe` is read on every call so a missing helper reports a clear error instead of breaking plugin load.
 * `finished` runs after every call, whatever its outcome, so the Changes panel can refresh.
 */
export function make(game: string, exe: () => string | undefined, finished: () => void = () => {}): Info[] {
  const call = async (
    command: string,
    request: Readonly<Record<string, unknown>>,
    timeoutMs: number,
    context: Context,
  ) => {
    const envelope = await Sidecar.run({
      exe: exe(),
      command,
      request: { ...request, gamePath: game },
      timeoutMs,
      signal: context.signal,
    }).finally(finished)
    return {
      content: Sidecar.format(envelope),
      metadata: {
        title: envelope.ok ? (envelope.summary ?? command) : (envelope.error?.message ?? `${command} failed`),
        ok: envelope.ok,
        ...(envelope.error ? { code: envelope.error.code } : {}),
      },
    }
  }

  return [
    {
      name: "game_detect",
      description:
        "Check the game in this folder: its name, Unity version, whether SpecTuner supports it, and what is already installed or changed. Call this first for every new request. Changes nothing.",
      input: NoInput,
      options: { codemode: false },
      execute: (_input: typeof NoInput.Type, context: Context) => call("detect", {}, 2 * Sidecar.MINUTE, context),
    },
    {
      name: "game_benchmark",
      description:
        "Start the game, measure how smoothly it runs, then close it. Takes one to a few minutes.\n" +
        "Without trySettings: measures the game as it is now. Returns avgFps, low1PctFps, frameMs, this PC's hardware, the resolution, the settings in effect, which saved overrides are in effect, loaded mods, log errors, and comparedToPrevious / comparedToBaseline (withinNoise: true means the difference is normal run-to-run variation, not a real change).\n" +
        'With trySettings: measures the game with and without those settings inside one run and saves nothing. Returns trial.verdict ("better", "worse", "no_clear_difference" or "not_applied"), trial.meanDeltaPct, trial.reason, and trial.a (without) and trial.b (with) numbers. To keep a "better" setting, call game_set_quality with the same settings.\n' +
        "In the result, mods lists every mod with loaded true or false, and pluginErrors lists errors per mod name (empty when none). game_build_mod already tests a new mod, so there is no need to run this after a build.\n" +
        "If the game fails to start, recent changes are undone automatically and listed in the result.",
      input: BenchmarkInput,
      options: { codemode: false },
      execute: (input: typeof BenchmarkInput.Type, context: Context) =>
        call("launch-and-benchmark", input, benchmarkTimeout(input), context),
    },
    {
      name: "game_set_quality",
      description:
        "Change graphics settings the game will use at its next start. Records one change that can be undone, and returns its change id and the overrides now in effect. Run game_benchmark afterwards to see the effect.",
      input: QualityInput,
      options: { codemode: false },
      execute: (input: typeof QualityInput.Type, context: Context) =>
        call("set-quality", input, 2 * Sidecar.MINUTE, context),
    },
    {
      name: "game_changes",
      description:
        "List every change SpecTuner made to this game, newest first, with its id, title, whether it is still active, and whether the game has started with it.\n" +
        'Also lists past changes (undone, removed by a restore, replaced, or removed because they failed) under "past", newest first, each with canReapply and, when that is false, whyNot: a plain sentence saying why it cannot be put back.',
      input: NoInput,
      options: { codemode: false },
      execute: (_input: typeof NoInput.Type, context: Context) => call("changes", {}, 2 * Sidecar.MINUTE, context),
    },
    {
      name: "game_revert",
      description:
        "Undo one change by its changeId, or set all to true to remove everything SpecTuner added and restore the game completely. Give exactly one of the two.",
      input: RevertInput,
      options: { codemode: false },
      execute: (input: typeof RevertInput.Type, context: Context) => call("revert", input, 2 * Sidecar.MINUTE, context),
    },
    {
      name: "game_search_code",
      description:
        "Search the game's own code. Returns hits, each with type, member, signature and line; text hits also have a one-line snippet. Use the type and member with game_read_code. The first search reads the game's code and takes a few seconds.",
      input: SearchInput,
      options: { codemode: false },
      execute: (input: typeof SearchInput.Type, context: Context) =>
        call("search-code", input, 5 * Sidecar.MINUTE, context),
    },
    {
      name: "game_read_code",
      description:
        'Read the game\'s code. With member: that member\'s code (all overloads). Without member: the whole type if it is short; if it is long, an outline listing its fields (with their types, such as "44: private float jumpHeight") and its methods, each with its line number. Then ask for one member, or pass startLine to read from a line. Every code line starts with "<line>: ".',
      input: ReadInput,
      options: { codemode: false },
      execute: (input: typeof ReadInput.Type, context: Context) =>
        call("read-code", input, 5 * Sidecar.MINUTE, context),
    },
    {
      name: "game_build_mod",
      description:
        "Compile a BepInEx 5 plugin from C# 9 source, install it as one change that can be undone, then start the game once to check it. Takes up to a minute; the game window opens and closes.\n" +
        "Success returns changeId and test: loaded (true or false), errorCount, errors, and logLines (the mod's own log lines from that start).\n" +
        "COMPILE_FAILED returns errors with line numbers and a hint for each: fix the source and call again; nothing was installed. " +
        "MOD_FAILED_TEST means it compiled but did not load, or logged errors, when the game started: it was removed again and the errors are in the result. " +
        "GAME_FAILED_TO_START means the game did not start with it; recent changes were undone and are listed. " +
        "INVALID_PLUGIN means the [BepInPlugin] class is wrong. UNSAFE_CODE means the code uses files, network, registry, processes, threads, timers, native calls or dynamic; remove that.",
      input: BuildInput,
      options: { codemode: false },
      // The build is quick; the time is for the one start of the game that checks the mod.
      execute: (input: typeof BuildInput.Type, context: Context) =>
        call("build-plugin", input, 10 * Sidecar.MINUTE, context),
    },
    {
      name: "game_read_logs",
      description:
        "Read the log of the game's last run. Returns lines (newest last, matching filter) and, separately, the last error lines. Use filter with your mod's display name to find its own lines.",
      input: LogsInput,
      options: { codemode: false },
      execute: (input: typeof LogsInput.Type, context: Context) =>
        call("read-logs", input, 2 * Sidecar.MINUTE, context),
    },
    {
      name: "game_reapply",
      description:
        "Put a past change back, by the id from game_changes' past list, instead of making it again. It becomes a new change with its own id that can be undone. Only for a past change with canReapply true.\n" +
        "A setting change is instant. A mod is rebuilt from its saved code and the game is started once to check it, like game_build_mod (the window opens and closes); if it fails it is removed again and the result says why.",
      input: ReapplyInput,
      options: { codemode: false },
      // A mod is rebuilt and checked with one start of the game, like game_build_mod.
      execute: (input: typeof ReapplyInput.Type, context: Context) =>
        call("reapply", input, 10 * Sidecar.MINUTE, context),
    },
  ]
}

/**
 * Room for start-up, warm-up and closing, plus the measuring itself. A trial measures 2 x pairs windows, each
 * followed by a settle pause, with the sidecar's defaults (the contract's example: 4 pairs, 5 s windows, 2 s settle).
 */
export function benchmarkTimeout(input: typeof BenchmarkInput.Type) {
  const measuring = input.trySettings ? 2 * 4 * (5 + 2) : (input.measureSeconds ?? 0)
  return 10 * Sidecar.MINUTE + measuring * 1000
}
