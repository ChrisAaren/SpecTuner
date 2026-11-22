import { expect, test } from "bun:test"
import { statSync } from "node:fs"
import { cp, mkdtemp, realpath, rm } from "node:fs/promises"
import { createRequire } from "node:module"
import os from "node:os"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"
import type { Configuration } from "electron-builder"
import { getConfig } from "./electron-builder.config"
import { verifyPreviewBinding } from "./script/preview-binding"

// Use electron-builder's matcher so the tests also cover its glob and directory traversal semantics.
const { FileMatcher } = createRequire(import.meta.resolve("electron-builder"))("app-builder-lib/out/fileMatcher")

const channels = [
  { channel: "dev", appId: "com.spectuner.desktop.dev", productName: "SpecTuner Dev" },
  { channel: "beta", appId: "com.spectuner.desktop.beta", productName: "SpecTuner Beta" },
  { channel: "prod", appId: "com.spectuner.desktop", productName: "SpecTuner" },
] as const

function load(channel: string, target?: string): Configuration {
  const previous = process.env.SPECTUNER_CHANNEL
  const previousTarget = process.env.SPECTUNER_TARGET
  process.env.SPECTUNER_CHANNEL = channel

  if (target !== undefined) process.env.SPECTUNER_TARGET = target
  else delete process.env.SPECTUNER_TARGET

  try {
    return getConfig()
  } finally {
    delete process.env.SPECTUNER_CHANNEL
    delete process.env.SPECTUNER_TARGET

    if (previous !== undefined) process.env.SPECTUNER_CHANNEL = previous

    if (previousTarget !== undefined) process.env.SPECTUNER_TARGET = previousTarget
  }
}

function trimFilter(dir: string, config: Configuration) {
  const patterns = Array.isArray(config.files) ? config.files.flatMap((value) => (value ? [String(value)] : [])) : []

  return new FileMatcher(dir, "", (value: string) => value, [
    "**/*",
    ...patterns.filter((value) => value.startsWith("!")),
  ]).createFilter()
}

test.each(channels)("channel identity for $channel", ({ channel, appId, productName }) => {
  const config = load(channel)
  expect(config.appId).toBe(appId)
  expect(config.productName).toBe(productName)
  expect(config.artifactName).toBe("spectuner-desktop-${os}-${arch}.${ext}")
  // The exe's version information names SpecTuner, not another company.
  expect(config.copyright).toMatch(/^Copyright © \d{4} SpecTuner$/)
  // SpecTuner has no update feed: upstream's would offer OpenCode as an update, and without a feed no app-update.yml
  // is written, so nothing updates the app.
  expect(config.publish).toBeNull()
  // The `opencode://` scheme belongs to an installed OpenCode; SpecTuner registers no scheme.
  expect(config.protocols).toBeUndefined()
  // Windows x64 only.
  expect(config.mac).toBeUndefined()
  expect(config.dmg).toBeUndefined()
  expect(config.linux).toBeUndefined()
  expect(config.deb).toBeUndefined()
  expect(config.rpm).toBeUndefined()
  expect(config.win?.target).toEqual(["nsis"])
})

test("the package names SpecTuner as its maker", async () => {
  const pkg = await Bun.file(path.join(import.meta.dirname, "package.json")).json()
  expect(pkg.author).toEqual({ name: "SpecTuner" })
  expect(pkg.description).toContain("SpecTuner")
  expect(JSON.stringify({ author: pkg.author, homepage: pkg.homepage, description: pkg.description })).not.toMatch(
    /opencode|anomaly/i,
  )
})

test.each([
  { channel: "dev", inspect: false },
  { channel: "beta", inspect: true },
  { channel: "prod", inspect: false },
])("Electron fuses for $channel", ({ channel, inspect }) => {
  // Absent keys keep Electron's defaults: NODE_OPTIONS stays on, the asar and file: fuses stay as they are.
  expect(load(channel).electronFuses).toEqual({
    enableCookieEncryption: true,
    runAsNode: false,
    enableNodeCliInspectArguments: inspect,
  })
})

test.each(channels)("every icon in the $channel icon folder is SpecTuner's", async ({ channel }) => {
  const icons = path.join(import.meta.dirname, "icons", channel)
  const branding = path.join(import.meta.dirname, "../../../branding")
  const files = (await Array.fromAsync(new Bun.Glob("**/*").scan({ cwd: icons }))).map((file) => file.replaceAll("\\", "/"))
  // icon.ico is the Windows icon; the web UI build reads its icons from packages/app/public (icons/README.md).
  expect(files.sort()).toEqual(["icon.ico"])

  // branding/ lives in the outer SpecTuner repository, beside this one.
  if (await Bun.file(path.join(branding, "icon.ico")).exists()) {
    const same = async (file: string, source: string) =>
      expect(
        Buffer.from(await Bun.file(path.join(icons, file)).arrayBuffer()).equals(
          Buffer.from(await Bun.file(path.join(branding, source)).arrayBuffer()),
        ),
      ).toBe(true)
    await same("icon.ico", "icon.ico")
  }
})

test("shared packaging defaults", async () => {
  const config = load("dev")
  // Packaging runs in secretless build jobs; signing happens afterwards.
  expect(config.win?.signtoolOptions).toBeUndefined()
  const include = path.join(import.meta.dirname, "resources/windows/installer.nsh")
  expect(config.nsis?.include).toBe(include)
  expect(await Bun.file(include).exists()).toBe(true)
  // The exe, the installer and the uninstaller carry the SpecTuner icon.
  expect(config.win?.icon).toBe("resources/icons/icon.ico")
  expect(config.nsis?.installerIcon).toBe("resources/icons/icon.ico")
  expect(config.nsis?.installerHeaderIcon).toBe("resources/icons/icon.ico")
  expect(config.nsis?.uninstallerIcon).toBe("resources/icons/icon.ico")
  expect(config.files).toContain("!resources/spectuner-server*")
  // The sidecar, tool plugin and prompt ship outside app.asar: the CLI loads the plugin and runs the sidecar from disk.
  expect(config.files).toContain("!resources/spectuner{,/**/*}")
  // The window reads its icon from <resources>/icons (src/main/windows/early.ts, appearance.ts), outside app.asar.
  expect(config.extraResources).toEqual([
    { from: "resources/", to: "", filter: ["spectuner-server.exe", "spectuner-server.version"] },
    { from: "resources/spectuner/", to: "spectuner", filter: ["**/*"] },
    { from: "resources/icons/", to: "icons", filter: ["icon.ico"] },
  ])
  const filter = trimFilter(import.meta.dirname, config)
  const file = statSync(import.meta.filename)
  const dir = statSync(import.meta.dirname)
  expect(filter(path.join(import.meta.dirname, "resources/spectuner"), dir)).toBe(false)
  expect(filter(path.join(import.meta.dirname, "resources/spectuner/spectuner-sidecar.exe"), file)).toBe(false)
  expect(filter(path.join(import.meta.dirname, "resources/spectuner/plugin/index.ts"), file)).toBe(false)
  expect(filter(path.join(import.meta.dirname, "resources/icons"), dir)).toBe(false)
  expect(filter(path.join(import.meta.dirname, "resources/icons/icon.ico"), file)).toBe(false)
})

test("trims external dependencies without excluding runtime files", () => {
  const fileStats = statSync(import.meta.filename)
  const dirStats = statSync(import.meta.dirname)
  const filter = trimFilter(import.meta.dirname, load("dev"))

  for (const prefix of ["node_modules/", "node_modules/parent/node_modules/"]) {
    const included = (file: string, stats = fileStats) => filter(path.join(import.meta.dirname, prefix, file), stats)

    // One file for each excluded pattern, including each brace alternative.
    for (const file of [
      "@zip.js/zip.js/dist/zip.js",
      ...["index.cjs", "index.min.js", "index-fflate.js", "deno.json", "eslint.config.mjs"].map(
        (name) => `@zip.js/zip.js/${name}`,
      ),
      ...["d.ts", "d.cts", "d.mts", "d.ts.map", "d.cts.map", "d.mts.map", "js.map", "cjs.map", "mjs.map"].map(
        (extension) => `unrelated/dist/index.${extension}`,
      ),
      "ajv/lib/core.ts",
      "ajv-formats/src/formats.ts",
      "js-yaml/dist/js-yaml.js",
      "js-yaml/dist/js-yaml.min.js",
      "js-yaml/dist/js-yaml.mjs.map",
      "js-yaml/bin/js-yaml.js",
    ]) {
      expect(included(file)).toBe(false)
    }

    for (const file of [
      "@zip.js/zip.js/index.js",
      "@zip.js/zip.js/lib/z-worker-inline.js",
      "ajv/dist/ajv.js",
      "ajv/dist/refs/json-schema-draft-07.json",
      "ajv-formats/dist/formats.js",
      "js-yaml/index.js",
      "js-yaml/dist/js-yaml.mjs",
      "unrelated/dist/index.js",
      "unrelated/dist/index.cjs",
      "unrelated/dist/data.json",
      "unrelated/src/index.ts",
      ...["@zip.js/zip.js", "ajv", "ajv-formats", "js-yaml"].flatMap(
        (name) => [`${name}/package.json`, `${name}/LICENSE`],
      ),
    ]) {
      expect(included(file)).toBe(true)
    }

    expect(included("@zip.js/zip.js/dist", dirStats)).toBe(false)
    expect(included("@zip.js/zip.js/lib", dirStats)).toBe(true)
  }

})

// The loader finds its binding beside it or, as an isolated install may lay it out, nested under it.
test.each([
  ["arm64", "win32-arm64-msvc", "beside"],
  ["x64", "win32-x64-msvc", "beside"],
  ["x64", "win32-x64-msvc", "nested"],
] as const)("packaging win32/%s requires its unpacked preview addon %s, %s the loader", async (arch, binding, layout) => {
  const resources = await realpath(await mkdtemp(path.join(os.tmpdir(), "preview-pack-")))
  const modules = path.join(resources, "app.asar.unpacked/node_modules")
  const name = `@yuuang/ffi-rs-${binding}`
  const pkg = path.join(layout === "nested" ? path.join(modules, "ffi-rs/node_modules") : modules, name)
  const addon = `ffi-rs.${binding}.node`

  try {
    await Bun.write(path.join(modules, "ffi-rs/package.json"), JSON.stringify({ main: "index.js", optionalDependencies: { [name]: "1.3.7" } }))
    await Bun.write(path.join(modules, "ffi-rs/index.js"), "module.exports = {}")
    await Bun.write(path.join(pkg, "package.json"), JSON.stringify({ main: addon, version: "1.3.7" }))

    if (process.platform === "win32" && arch === process.arch) {
      const source = createRequire(import.meta.resolve("ffi-rs")).resolve(name)
      await cp(source, path.join(pkg, addon))
      await verifyPreviewBinding(resources, arch)
      // A binding of another version than the loader declares is not the one it was built against.
      await Bun.write(path.join(pkg, "package.json"), JSON.stringify({ main: addon, version: "1.3.2" }))
      await expect(verifyPreviewBinding(resources, arch)).rejects.toThrow(addon)
      await Bun.write(path.join(pkg, "package.json"), JSON.stringify({ main: addon, version: "1.3.7" }))
      await rm(path.join(pkg, addon))
    }

    await expect(verifyPreviewBinding(resources, arch)).rejects.toThrow(addon)
    await Bun.write(path.join(pkg, addon), "not a native library")
    await expect(verifyPreviewBinding(resources, arch)).rejects.toThrow(addon)
  } finally {
    await rm(resources, { recursive: true, force: true })
  }
})

test("the trimmed Zip.js package can still export compressed logs", async () => {
  const config = load("dev")
  const dir = await mkdtemp(path.join(os.tmpdir(), "spectuner-zip-package-"))
  const source = path.dirname(fileURLToPath(import.meta.resolve("@zip.js/zip.js/package.json")))
  const filter = trimFilter(dir, config)

  try {
    await cp(source, dir, {
      recursive: true,
      filter: (file) =>
        filter(path.join(dir, "node_modules/@zip.js/zip.js", path.relative(source, file)), statSync(file)),
    })
    const zip = await import(pathToFileURL(path.join(dir, "index.js")).href)
    const writer = new zip.ZipWriter(new zip.BlobWriter("application/zip"))
    await writer.add("desktop.log", new zip.BlobReader(new Blob(["diagnostic log\n".repeat(100)])))
    const reader = new zip.ZipReader(new zip.BlobReader(await writer.close()))
    const entries = await reader.getEntries()
    expect(entries.map((entry: { filename: string }) => entry.filename)).toEqual(["desktop.log"])
    expect(entries[0].compressionMethod).toBe(8)
    expect(await entries[0].getData(new zip.TextWriter())).toBe("diagnostic log\n".repeat(100))
    await reader.close()
    await zip.terminateWorkers()
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})
