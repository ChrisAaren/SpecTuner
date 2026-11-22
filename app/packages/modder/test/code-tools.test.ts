import { describe, expect, test } from "bun:test"
import { Exit, Schema } from "effect"
import { SpecTunerTools } from "../plugin/tools.ts"

const ok = (schema: Schema.Top, input: unknown) => {
  const exit = Schema.decodeUnknownExit(schema as Schema.Codec<unknown, unknown>)(input)
  return Exit.isSuccess(exit) ? exit.value : undefined
}

describe("code tool inputs follow the sidecar contract", () => {
  test("game_search_code: query of 2+ characters, limit 1..50, optional assembly", () => {
    expect(
      ok(SpecTunerTools.SearchInput, { query: "jump", kind: "name", limit: 50, assembly: "UnityEngine.CoreModule" }),
    ).toEqual({
      query: "jump",
      kind: "name",
      limit: 50,
      assembly: "UnityEngine.CoreModule",
    })
    expect(ok(SpecTunerTools.SearchInput, { query: "j" })).toBeUndefined()
    expect(ok(SpecTunerTools.SearchInput, { query: "jump", limit: 51 })).toBeUndefined()
  })

  test("game_read_code: startLine and assembly pass through, maxLines 10..400", () => {
    expect(ok(SpecTunerTools.ReadInput, { type: "SampleGame.PlayerController", startLine: 150, maxLines: 400 })).toEqual(
      {
        type: "SampleGame.PlayerController",
        startLine: 150,
        maxLines: 400,
      },
    )
    expect(ok(SpecTunerTools.ReadInput, { type: "T", member: "Update", assembly: "Assembly-CSharp" })).toEqual({
      type: "T",
      member: "Update",
      assembly: "Assembly-CSharp",
    })
    expect(ok(SpecTunerTools.ReadInput, { type: "T", maxLines: 9 })).toBeUndefined()
    expect(ok(SpecTunerTools.ReadInput, { type: "T", maxLines: 401 })).toBeUndefined()
  })

  test("game_build_mod: name is letters and digits, at most 40, never SpecTuner", () => {
    const build = (name: string) => ok(SpecTunerTools.BuildInput, { name, title: "t", description: "d", source: "x" })
    expect(build("DoubleJump")).toBeDefined()
    expect(build("A".repeat(40))).toBeDefined()
    expect(build("A".repeat(41))).toBeUndefined()
    expect(build("SpecTuner")).toBeUndefined()
    expect(build("spectuner")).toBeUndefined()
    expect(build("Double Jump")).toBeUndefined()
    expect(build("1Jump")).toBeUndefined()
  })

  test("game_read_logs: tail 1..200", () => {
    expect(ok(SpecTunerTools.LogsInput, { filter: "Double Jump", tail: 200 })).toEqual({
      filter: "Double Jump",
      tail: 200,
    })
    expect(ok(SpecTunerTools.LogsInput, { tail: 201 })).toBeUndefined()
  })
})
