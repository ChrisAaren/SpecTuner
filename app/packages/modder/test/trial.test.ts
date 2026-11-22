import { describe, expect, test } from "bun:test"
import { Exit, Schema } from "effect"
import { SpecTunerTools } from "../plugin/tools.ts"

const decode = Schema.decodeUnknownExit(SpecTunerTools.BenchmarkInput)

describe("game_benchmark trial inputs", () => {
  test("a trial passes label and trySettings through", () => {
    const input = { label: "try", trySettings: { shadowDistance: 20, shadows: "Disable" } }
    const exit = decode(input)
    expect(Exit.isSuccess(exit) && exit.value).toEqual(input)
  })

  test("measurement knobs a model adds are dropped, so the sidecar's defaults apply", () => {
    const exit = decode({
      label: "try",
      trySettings: { shadowDistance: 20 },
      pairs: 2,
      windowSeconds: 3,
      settleSeconds: 1,
      warmupSeconds: 0,
    })
    expect(Exit.isSuccess(exit) && exit.value).toEqual({ label: "try", trySettings: { shadowDistance: 20 } })
  })

  test("keys a trial refuses, unknown keys and an empty trySettings fail before the sidecar runs", () => {
    for (const trySettings of [{ qualityLevel: 1 }, { resolutionWidth: 1280 }, { vSyncCount: 0 }, { bogus: 1 }, {}])
      expect(Exit.isFailure(decode({ label: "try", trySettings }))).toBe(true)
  })

  test("measureSeconds follows the contract's range", () => {
    expect(Exit.isFailure(decode({ label: "x", measureSeconds: 0 }))).toBe(true)
    expect(Exit.isFailure(decode({ label: "x", measureSeconds: 301 }))).toBe(true)
    expect(Exit.isSuccess(decode({ label: "x", measureSeconds: 5 }))).toBe(true)
  })

  test("the trial keys are the quality keys minus the ones the contract refuses", () => {
    expect(SpecTunerTools.TRIAL_KEYS).not.toContain("qualityLevel")
    expect(SpecTunerTools.TRIAL_KEYS).not.toContain("fullscreenMode")
    expect(SpecTunerTools.TRIAL_KEYS).not.toContain("targetFrameRate")
    expect(SpecTunerTools.TRIAL_KEYS).toContain("shadowDistance")
    expect(SpecTunerTools.TRIAL_KEYS).toContain("renderScale")
  })

  test("the timeout covers a trial with the sidecar's default rounds", () => {
    const minutes10 = 10 * 60_000
    expect(SpecTunerTools.benchmarkTimeout({ label: "x", measureSeconds: 20 })).toBe(minutes10 + 20_000)
    // Defaults 4 pairs x 2 windows x (5 s + 2 s settle) = 56 s.
    expect(SpecTunerTools.benchmarkTimeout({ label: "x", trySettings: { lodBias: 0.7 } })).toBe(minutes10 + 56_000)
  })
})
