import { expect, test } from "bun:test"
import { statSync } from "node:fs"
import { cp, mkdtemp, realpath, rm, writeFile } from "node:fs/promises"
import { createRequire } from "node:module"
import os from "node:os"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"
import type { Configuration } from "electron-builder"
import { getConfig, iconResources, linuxExecutableName, MAC_FOLDER_PROMPTS, spectunerResources } from "./electron-builder.config"
import { NativeArch } from "../../script/native-arch"
import { verifyPreviewBinding } from "./script/preview-binding"

// Use electron-builder's matcher so the tests also cover its glob and directory traversal semantics.
const { FileMatcher } = createRequire(import.meta.resolve("electron-builder"))("app-builder-lib/out/fileMatcher")

const channels = [
  { channel: "dev", appId: "com.spectuner.desktop.dev", productName: "SpecTuner Dev" },
  { channel: "beta", appId: "com.spectuner.desktop.beta", productName: "SpecTuner Beta" },
  { channel: "prod", appId: "com.spectuner.desktop", productName: "SpecTuner" },
] as const

// The Windows target unless a test names another, so the Windows cases run on every host.
function load(channel: string, target: string | undefined = "windows-x64"): Configuration {
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
  // A Windows build carries only Windows packaging.
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
  // icon.ico is the Windows icon, icon.icns the macOS one, the `<size>x<size>.png` files the Linux icon theme and
  // icon.png the Linux window icon. The web UI build reads its icons from packages/app/public (icons/README.md).
  expect(files.sort()).toEqual(Object.keys(ICON_SOURCES).sort())

  // branding/ lives in the outer SpecTuner repository, beside this one.
  if (await Bun.file(path.join(branding, "icon.ico")).exists()) {
    const same = async (file: string, source: string) =>
      expect(
        Buffer.from(await Bun.file(path.join(icons, file)).arrayBuffer()).equals(
          Buffer.from(await Bun.file(path.join(branding, source)).arrayBuffer()),
        ),
      ).toBe(true)
    for (const [file, source] of Object.entries(ICON_SOURCES)) await same(file, source)
  }
})

// Each file in icons/<channel>/ and the branding/ output it is a copy of.
const ICON_SOURCES: Record<string, string> = {
  "icon.ico": "icon.ico",
  "icon.icns": "icon.icns",
  "icon.png": "icon-512.png",
  ...Object.fromEntries([16, 24, 32, 48, 64, 128, 256, 512].map((size) => [`${size}x${size}.png`, `icon-${size}.png`])),
}

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

const targets = [
  { target: "linux-x64", os: "linux", server: "spectuner-server", sidecar: "spectuner-sidecar", icons: ["icon.png"] },
  { target: "darwin-arm64", os: "darwin", server: "spectuner-server", sidecar: "spectuner-sidecar", icons: [] },
  { target: "windows-x64", os: "win32", server: "spectuner-server.exe", sidecar: "spectuner-sidecar.exe", icons: ["icon.ico"] },
] as const

test.each(targets)("$target ships its own server, sidecar and window icon outside app.asar", ({ target, os, server, sidecar, icons }) => {
  const config = load("prod", target)
  expect(spectunerResources(os)).toEqual([sidecar, "modder.txt", "plugin/index.ts"])
  expect(iconResources(os)).toEqual(icons)
  expect(config.extraResources).toEqual([
    { from: "resources/", to: "", filter: [server, "spectuner-server.version"] },
    { from: "resources/spectuner/", to: "spectuner", filter: ["**/*"] },
    ...(icons.length ? [{ from: "resources/icons/", to: "icons", filter: [...icons] }] : []),
  ])
  const filter = trimFilter(import.meta.dirname, config)
  const file = statSync(import.meta.filename)
  expect(filter(path.join(import.meta.dirname, "resources/spectuner", sidecar), file)).toBe(false)
  expect(filter(path.join(import.meta.dirname, "resources", server), file)).toBe(false)
})

test.each(["dev", "beta", "prod"] as const)("macOS %s: unsigned dmg and zip, ad hoc signature, plain folder prompts", (channel) => {
  const config = load(channel, "darwin-arm64")
  expect(config.mac).toEqual({
    category: "public.app-category.utilities",
    icon: "resources/icons/icon.icns",
    target: ["dmg", "zip"],
    identity: "-",
    hardenedRuntime: false,
    extendInfo: MAC_FOLDER_PROMPTS,
  })
  expect(config.dmg).toEqual({ icon: "resources/icons/icon.icns" })
  // Flipping a fuse breaks the signature; without any valid signature Apple Silicon refuses to start the app.
  expect(config.electronFuses?.resetAdHocDarwinSignature).toBe(true)
  expect(config.linux).toBeUndefined()
  expect(config.deb).toBeUndefined()
  // Every prompt names SpecTuner and says why, in words a player understands.
  for (const text of Object.values(MAC_FOLDER_PROMPTS)) expect(text).toMatch(/^SpecTuner needs to open .+ to find and change games/)
  // ffi-rs is used only on Windows, so neither its loader nor a binding ships.
  expect(config.asarUnpack).toBeUndefined()
  expect(config.files).toContain("!**/node_modules/ffi-rs{,/**/*}")
  expect(config.files).toContain("!**/node_modules/@yuuang/ffi-rs-*{,/**/*}")
})

test.each([
  { channel: "dev", appId: "com.spectuner.desktop.dev", executable: "com.spectuner.desktop.dev" },
  { channel: "beta", appId: "com.spectuner.desktop.beta", executable: "com.spectuner.desktop.beta" },
  { channel: "prod", appId: "com.spectuner.desktop", executable: "com.spectuner" },
] as const)("Linux $channel: AppImage and deb, launcher matched to the window", ({ channel, appId, executable }) => {
  const config = load(channel, "linux-x64")
  expect(linuxExecutableName(appId)).toBe(executable)
  expect(config.linux).toMatchObject({
    icon: "resources/icons",
    category: "Utility",
    executableName: executable,
    syncDesktopName: true,
    desktop: { entry: { StartupWMClass: appId } },
    target: ["AppImage", "deb"],
  })
  // The executable never ends in `.desktop`, which AppImage integration tools would read as a launcher.
  expect(config.linux?.executableName).not.toMatch(/\.desktop$/)
  expect(config.extraMetadata).toMatchObject({ desktopName: `${appId}.desktop` })
  expect(config.deb).toEqual({ packageName: { dev: "spectuner-dev", beta: "spectuner-beta", prod: "spectuner" }[channel] })
  expect(config.mac).toBeUndefined()
  expect(config.rpm).toBeUndefined()
  expect(config.electronFuses?.resetAdHocDarwinSignature).toBeUndefined()
  expect(config.asarUnpack).toBeUndefined()
})

test("a Windows build's metadata carries only the version, as before; Linux adds the launcher name and homepage", () => {
  const keys = ["SPECTUNER_VERSION", "SPECTUNER_HOMEPAGE", "GITHUB_SERVER_URL", "GITHUB_REPOSITORY"] as const
  const saved = Object.fromEntries(keys.map((key) => [key, process.env[key]]))
  keys.forEach((key) => delete process.env[key])

  try {
    expect(load("prod", "windows-x64").extraMetadata).toBeUndefined()
    process.env.SPECTUNER_VERSION = "9.9.9"
    expect(load("prod", "windows-x64").extraMetadata).toEqual({ version: "9.9.9" })
    expect(load("prod", "linux-x64").extraMetadata).toEqual({ desktopName: "com.spectuner.desktop.desktop", version: "9.9.9" })
    // The deb needs a homepage: a GitHub Actions run's repository, or SPECTUNER_HOMEPAGE.
    process.env.GITHUB_SERVER_URL = "https://github.example"
    process.env.GITHUB_REPOSITORY = "owner/repo"
    expect(load("prod", "linux-x64").extraMetadata).toMatchObject({ homepage: "https://github.example/owner/repo" })
    process.env.SPECTUNER_HOMEPAGE = "https://spectuner.example"
    expect(load("prod", "linux-x64").extraMetadata).toMatchObject({ homepage: "https://spectuner.example" })
    expect(load("prod", "windows-x64").extraMetadata).toEqual({ version: "9.9.9" })
    expect(load("prod", "linux-x64").linux?.maintainer).toBe("SpecTuner")
  } finally {
    keys.forEach((key) => {
      if (saved[key] === undefined) delete process.env[key]
      else process.env[key] = saved[key]
    })
  }
})

test("the sidecar check looks at the program's own header, not at the .NET assemblies packed inside it", async () => {
  // A .NET single-file program: a native host, then managed PE images. A precompiled assembly carries a machine value
  // of its own (0xfd1d on Linux x64), which the whole-file check takes for foreign code.
  const managed = Buffer.alloc(0x100)
  managed.write("MZ", 0, "latin1")
  managed.writeUInt32LE(0x80, 0x3c)
  managed.writeUInt32LE(0x4550, 0x80)
  managed.writeUInt16LE(0xfd1d, 0x84)
  const elf = Buffer.alloc(0x40)
  elf.write("\x7fELF", 0, "latin1")
  elf.set([2, 1, 1], 4)
  elf.writeUInt16LE(62, 18)
  elf.writeUInt32LE(1, 20)
  const macho = Buffer.alloc(0x40)
  macho.writeUInt32LE(0xfeedfacf, 0)
  macho.writeUInt32LE(0x0100000c, 4)

  const dir = await mkdtemp(path.join(os.tmpdir(), "spectuner-sidecar-host-"))
  try {
    const linux = path.join(dir, "linux")
    const mac = path.join(dir, "mac")
    const text = path.join(dir, "text")
    await writeFile(linux, Buffer.concat([elf, managed]))
    await writeFile(mac, Buffer.concat([macho, managed]))
    await writeFile(text, "#!/bin/sh\n")

    await expect(NativeArch.verify(linux, "x64")).rejects.toThrow(/must contain only x64 code/)
    await NativeArch.verifyHost(linux, "ELF", "x64")
    await NativeArch.verifyHost(mac, "Mach-O", "arm64")
    await expect(NativeArch.verifyHost(linux, "ELF", "arm64")).rejects.toThrow(/must be a ELF arm64 program, but is ELF x64/)
    await expect(NativeArch.verifyHost(linux, "Mach-O", "x64")).rejects.toThrow(/but is ELF x64/)
    await expect(NativeArch.verifyHost(mac, "Mach-O", "x64")).rejects.toThrow(/but is Mach-O arm64/)
    await expect(NativeArch.verifyHost(text, "ELF", "x64")).rejects.toThrow(/is not a native executable/)
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})
