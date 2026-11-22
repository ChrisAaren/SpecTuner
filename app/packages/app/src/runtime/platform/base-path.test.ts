import { describe, expect, test } from "bun:test"
import { normalizeBasePath, stripBasePath, withBasePath } from "./base-path"

describe("normalizeBasePath", () => {
  test("is empty at the root", () => {
    expect(normalizeBasePath(undefined)).toBe("")
    expect(normalizeBasePath("/")).toBe("")
    expect(normalizeBasePath("")).toBe("")
  })

  test("has a leading slash and no trailing slash", () => {
    expect(normalizeBasePath("/spectuner/")).toBe("/spectuner")
    expect(normalizeBasePath("spectuner")).toBe("/spectuner")
    expect(normalizeBasePath("//a/b//")).toBe("/a/b")
  })
})

describe("withBasePath", () => {
  test("leaves paths alone at the root", () => {
    expect(withBasePath("/new-session?x=1", "")).toBe("/new-session?x=1")
  })

  test("prefixes paths with the base", () => {
    expect(withBasePath("/", "/spectuner")).toBe("/spectuner/")
    expect(withBasePath("/server/a/session/b#m", "/spectuner")).toBe("/spectuner/server/a/session/b#m")
  })
})

describe("stripBasePath", () => {
  test("leaves paths alone at the root", () => {
    expect(stripBasePath("/new-session", "")).toBe("/new-session")
  })

  test("removes the base", () => {
    expect(stripBasePath("/spectuner", "/spectuner")).toBe("/")
    expect(stripBasePath("/spectuner/", "/spectuner")).toBe("/")
    expect(stripBasePath("/spectuner?draftId=1", "/spectuner")).toBe("/?draftId=1")
    expect(stripBasePath("/spectuner/new-session#top", "/spectuner")).toBe("/new-session#top")
  })

  test("keeps paths outside the base", () => {
    expect(stripBasePath("/spectuner-other/x", "/spectuner")).toBe("/spectuner-other/x")
    expect(stripBasePath("/elsewhere", "/spectuner")).toBe("/elsewhere")
  })

  test("round-trips with withBasePath", () => {
    for (const path of ["/", "/new-session", "/server/a/session/b?q=1#h"])
      expect(stripBasePath(withBasePath(path, "/spectuner"), "/spectuner")).toBe(path)
  })
})
