import { expect, test } from "bun:test"
import path from "node:path"
import { iconResources } from "../../../electron-builder.config"
import { windowIconFile } from "./icon"

test.each([
  ["win32", "icon.ico"],
  ["linux", "icon.png"],
  ["darwin", undefined],
] as const)("%s windows use %s", (platform, file) => {
  expect(windowIconFile(platform)).toBe(file)
  // Packaging ships exactly the file the window reads.
  expect(iconResources(platform)).toEqual(file ? [file] : [])
})

// Every app window is created in one of these two places; each must take its icon from windowIconFile.
test.each(["early.ts", "appearance.ts"])("%s reads the window icon from windowIconFile", async (file) => {
  const source = await Bun.file(path.join(import.meta.dirname, file)).text()
  expect(source).toContain("windowIconFile()")
  expect(source).not.toContain('"icon.ico"')
})
