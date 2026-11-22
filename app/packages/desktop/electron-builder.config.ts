import { stat } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

import { Arch, type Configuration } from "electron-builder"
import { NativeArch } from "../../script/native-arch"
import { getCurrentCli } from "./script/utils"
import { previewBinding, verifyPreviewBinding } from "./script/preview-binding"

// SpecTuner ships for Windows x64 only: an NSIS installer around the unpacked app.
const packageDir = path.dirname(fileURLToPath(import.meta.url))

function resolveChannel() {
  const raw = process.env.SPECTUNER_CHANNEL

  if (raw === "dev" || raw === "beta" || raw === "prod") return raw

  if (raw === "latest") return "prod"

  return "dev"
}

// SpecTuner's own identity (keep in sync with src/main/constants.ts): its own install directory, userData, taskbar
// identity and single-instance lock, never an installed OpenCode's.
const APP_IDS = {
  dev: "com.spectuner.desktop.dev",
  beta: "com.spectuner.desktop.beta",
  prod: "com.spectuner.desktop",
} as const

const PRODUCT_NAMES = {
  dev: "SpecTuner Dev",
  beta: "SpecTuner Beta",
  prod: "SpecTuner",
} as const

// Files packaging requires in resources/spectuner (filled by script/stage-spectuner.ts), shipped outside app.asar.
export const SPECTUNER_RESOURCES = ["spectuner-sidecar.exe", "modder.txt", "plugin/index.ts"] as const

// The window icon, read from `<resources>/icons` (src/main/windows/early.ts and appearance.ts). The exe, the installer
// and the uninstaller embed the same file through `win.icon` and `nsis.installerIcon`.
export const ICON_RESOURCES = ["icon.ico"] as const

const getBase = (): Configuration => {
  const target = getCurrentCli()
  const binding = previewBinding(target.os, target.cpu)

  return {
    artifactName: "spectuner-desktop-${os}-${arch}.${ext}",
    directories: {
      output: "dist",
      buildResources: "resources",
    },
    // Release builds take their version from SPECTUNER_VERSION instead of rewriting package.json.
    extraMetadata: process.env.SPECTUNER_VERSION ? { version: process.env.SPECTUNER_VERSION } : undefined,
    // The exe's version information (CompanyName comes from package.json's author, SpecTuner).
    copyright: `Copyright © ${new Date().getFullYear()} SpecTuner`,
    files: [
      "out/**/*",
      "resources/**/*",
      "!resources/spectuner-server*",
      "!resources/spectuner{,/**/*}",
      // The icons ship outside app.asar (extraResources below), where the window looks for them.
      "!resources/icons{,/**/*}",
      `!**/node_modules/@yuuang/ffi-rs-!(${binding}){,/**/*}`,
      // Log export imports Zip.js as ESM. Keep index.js and lib, including its inline worker.
      "!**/node_modules/@zip.js/zip.js/dist{,/**/*}",
      "!**/node_modules/@zip.js/zip.js/{index.cjs,index.min.js,index-fflate.js,deno.json,eslint.config.mjs}",
      // Nothing executes type declarations or source maps, and every entry costs startup time: the
      // main process parses the whole asar header before it runs any JavaScript.
      "!**/node_modules/**/*.d.{ts,cts,mts}",
      "!**/node_modules/**/*.d.{ts,cts,mts}.map",
      "!**/node_modules/**/*.{js,cjs,mjs}.map",
      // These packages execute compiled JavaScript, not their sources.
      "!**/node_modules/ajv/lib{,/**/*}",
      "!**/node_modules/ajv-formats/src{,/**/*}",
      // Keep js-yaml's CommonJS sources and dist/js-yaml.mjs ESM entry, not browser bundles or its CLI.
      "!**/node_modules/js-yaml/dist/{js-yaml.js,js-yaml.min.js,*.map}",
      "!**/node_modules/js-yaml/bin{,/**/*}",
    ],
    asarUnpack: ["**/node_modules/ffi-rs/**/*", `**/node_modules/@yuuang/ffi-rs-${binding}/**/*`],
    extraResources: [
      {
        from: "resources/",
        to: "",
        filter: ["spectuner-server.exe", "spectuner-server.version"],
      },
      { from: "resources/spectuner/", to: "spectuner", filter: ["**/*"] },
      { from: "resources/icons/", to: "icons", filter: [...ICON_RESOURCES] },
    ],
    // Electron stores cookies in plain text by default. The browser pane keeps sign-ins, so encrypt them with the OS
    // key store (DPAPI on Windows). This is one-way: turning it off again loses every cookie.
    // https://www.electronjs.org/docs/latest/tutorial/fuses#cookieencryption
    electronFuses: {
      enableCookieEncryption: true,
      // Started as plain Node (ELECTRON_RUN_AS_NODE) or with an inspector, the binary would run any code under the
      // app's identity. Nothing runs the app as Node: the CLI is its own executable. Beta keeps --inspect, as it keeps
      // its on-request debugging port, for the startup benchmark's Node timings (perf/startup.bench.ts).
      runAsNode: false,
      enableNodeCliInspectArguments: resolveChannel() === "beta",
      // NODE_OPTIONS stays: a packaged app already ignores every option but --http-parser and --max-http-header-size,
      // and turning the fuse off would also strip NODE_EXTRA_CA_CERTS from the environment the CLI and its terminals get.
    },
    afterPack: async (context) => {
      const arch = Arch[context.arch]

      if (context.electronPlatformName !== "win32")
        throw new Error(`SpecTuner packages for Windows only, not ${context.electronPlatformName}`)

      if (arch !== "x64" && arch !== "arm64") throw new Error(`Unsupported desktop architecture: ${arch}`)

      // The --x64 flag picks Electron while SPECTUNER_TARGET picks the CLI, so they can disagree.
      if (arch !== target.cpu) throw new Error(`Packaging ${arch} Electron with the ${target.target} CLI`)
      const resources = context.packager.getResourcesDir(context.appOutDir)
      await verifyPreviewBinding(resources, arch)
      const cli = path.join(resources, "spectuner-server.exe")

      const file = await stat(cli)

      if (!file.isFile() || file.size === 0) throw new Error(`Bundled CLI must be a non-empty file: ${cli}`)
      await NativeArch.verify(cli, arch)
      const version = path.join(path.dirname(cli), "spectuner-server.version")

      if ((await stat(version)).size === 0) throw new Error(`Bundled CLI version must be a non-empty file: ${version}`)

      // SpecTuner's sidecar, tool plugin and modder prompt: the app's server cannot work without them.
      await Promise.all(
        SPECTUNER_RESOURCES.map(async (name) => {
          const file = path.join(resources, "spectuner", name)
          const info = await stat(file).catch(() => undefined)

          if (!info?.isFile() || info.size === 0)
            throw new Error(`Bundled SpecTuner resource must be a non-empty file: ${file}. Run script/stage-spectuner.ts`)
        }),
      )

      // Without it the window and the taskbar entry would fall back to the exe's embedded icon.
      await Promise.all(
        ICON_RESOURCES.map(async (name) => {
          const file = path.join(resources, "icons", name)
          const info = await stat(file).catch(() => undefined)

          if (!info?.isFile() || info.size === 0)
            throw new Error(`Bundled icon must be a non-empty file: ${file}. Run script/copy-icons.ts`)
        }),
      )
    },
    win: {
      icon: `resources/icons/icon.ico`,
      target: ["nsis"],
      verifyUpdateCodeSignature: false,
    },
    nsis: {
      include: path.join(packageDir, "resources", "windows", "installer.nsh"),
      oneClick: true,
      perMachine: false,
      installerIcon: `resources/icons/icon.ico`,
      installerHeaderIcon: `resources/icons/icon.ico`,
      uninstallerIcon: `resources/icons/icon.ico`,
    },
  }
}

export function getConfig(): Configuration {
  const channel = resolveChannel()

  return {
    ...getBase(),
    appId: APP_IDS[channel],
    productName: PRODUCT_NAMES[channel],
    // No update feed: upstream's would offer OpenCode as an update, and SpecTuner does not update itself.
    publish: null,
  }
}

export default getConfig()
