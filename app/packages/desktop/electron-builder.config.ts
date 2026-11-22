import { stat } from "node:fs/promises"
import path from "node:path"
import { fileURLToPath } from "node:url"

import { Arch, type Configuration } from "electron-builder"
import { SpecTunerEnv } from "@spectuner/modder/env"
import { NativeArch } from "../../script/native-arch"
import { getCurrentCli, serverFile } from "./script/utils"
import { previewBinding, verifyPreviewBinding } from "./script/preview-binding"

// SpecTuner ships for Windows x64 (an NSIS installer around the unpacked app), Linux x64 (AppImage and deb) and macOS on
// Apple Silicon (dmg and zip, signed ad hoc only). The target comes from SPECTUNER_TARGET, else from the host.
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

// Files packaging requires in resources/spectuner (filled by script/stage-spectuner.ts), shipped outside app.asar. The
// sidecar is `spectuner-sidecar.exe` on Windows and `spectuner-sidecar` elsewhere.
export function spectunerResources(os: string) {
  return [SpecTunerEnv.sidecarFile(os as NodeJS.Platform), "modder.txt", "plugin/index.ts"] as const
}

// The window icon, read from `<resources>/icons` (src/main/windows/early.ts and appearance.ts). On Windows the exe, the
// installer and the uninstaller embed the same file through `win.icon` and `nsis.installerIcon`. Linux windows take a
// PNG. macOS draws windows with the app bundle's icon (`mac.icon`), so it ships none here.
export function iconResources(os: string): readonly string[] {
  if (os === "win32") return ["icon.ico"]

  if (os === "linux") return ["icon.png"]

  return []
}

// What macOS shows when the app (or the server and sidecar it starts) first opens a protected folder, such as a game
// kept in Documents or on an external drive. Plain words for a player.
export const MAC_FOLDER_PROMPTS = {
  NSDocumentsFolderUsageDescription: "SpecTuner needs to open your Documents folder to find and change games you keep there.",
  NSDesktopFolderUsageDescription: "SpecTuner needs to open your Desktop folder to find and change games you keep there.",
  NSDownloadsFolderUsageDescription: "SpecTuner needs to open your Downloads folder to find and change games you keep there.",
  NSNetworkVolumesUsageDescription: "SpecTuner needs to open network drives to find and change games you keep there.",
  NSRemovableVolumesUsageDescription: "SpecTuner needs to open external drives to find and change games you keep there.",
} as const

// The prod app ID ends in `.desktop`; strip it for the Linux executable name (see `linux.executableName`).
export function linuxExecutableName(appId: string) {
  return appId.replace(/\.desktop$/, "")
}

// The deb package name per channel: `spectuner`, `spectuner-beta`, `spectuner-dev`.
export function linuxPackageName(appId: string) {
  return appId.replace(/^com\.spectuner\.desktop/, "spectuner").replaceAll(".", "-")
}

const getBase = (appId: string): Configuration => {
  const target = getCurrentCli()
  const binding = previewBinding(target.os, target.cpu)
  const server = serverFile(target.os)
  const icons = iconResources(target.os)

  return {
    artifactName: "spectuner-desktop-${os}-${arch}.${ext}",
    directories: {
      output: "dist",
      buildResources: "resources",
    },
    // Release builds take their version from SPECTUNER_VERSION instead of rewriting package.json.
    // On Linux `desktopName` names the launcher file and the window class (`linux.syncDesktopName`).
    extraMetadata: metadata(target.os === "linux" ? { desktopName: `${appId}.desktop`, ...homepage() } : {}),
    // The exe's version information (CompanyName comes from package.json's author, SpecTuner).
    copyright: `Copyright © ${new Date().getFullYear()} SpecTuner`,
    files: [
      "out/**/*",
      "resources/**/*",
      "!resources/spectuner-server*",
      "!resources/spectuner{,/**/*}",
      // The icons ship outside app.asar (extraResources below), where the window looks for them.
      "!resources/icons{,/**/*}",
      // ffi-rs is loaded only on Windows; elsewhere neither the loader nor any binding ships.
      ...(binding
        ? [`!**/node_modules/@yuuang/ffi-rs-!(${binding}){,/**/*}`]
        : ["!**/node_modules/ffi-rs{,/**/*}", "!**/node_modules/@yuuang/ffi-rs-*{,/**/*}"]),
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
    ...(binding ? { asarUnpack: ["**/node_modules/ffi-rs/**/*", `**/node_modules/@yuuang/ffi-rs-${binding}/**/*`] } : {}),
    extraResources: [
      {
        from: "resources/",
        to: "",
        filter: [server, "spectuner-server.version"],
      },
      { from: "resources/spectuner/", to: "spectuner", filter: ["**/*"] },
      ...(icons.length ? [{ from: "resources/icons/", to: "icons", filter: [...icons] }] : []),
    ],
    // Electron stores cookies in plain text by default. The browser pane keeps sign-ins, so encrypt them with the OS
    // key store (DPAPI on Windows, Keychain on macOS, libsecret or kwallet on Linux). This is one-way: turning it off
    // again loses every cookie.
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
      // Flipping a fuse breaks the macOS signature, and SpecTuner has no signing identity, so the app is signed again ad
      // hoc; without any valid signature Apple Silicon refuses to start it.
      ...(target.os === "darwin" ? { resetAdHocDarwinSignature: true } : {}),
    },
    afterPack: async (context) => {
      const arch = Arch[context.arch]

      // The --win/--linux/--mac and --x64/--arm64 flags pick Electron while SPECTUNER_TARGET picks the CLI, so they
      // can disagree.
      if (context.electronPlatformName !== target.os)
        throw new Error(`Packaging ${context.electronPlatformName} Electron with the ${target.target} CLI`)

      if (arch !== "x64" && arch !== "arm64") throw new Error(`Unsupported desktop architecture: ${arch}`)

      if (arch !== target.cpu) throw new Error(`Packaging ${arch} Electron with the ${target.target} CLI`)
      const resources = context.packager.getResourcesDir(context.appOutDir)

      if (target.os === "win32") await verifyPreviewBinding(resources, arch)
      const cli = path.join(resources, server)

      const file = await stat(cli)

      if (!file.isFile() || file.size === 0) throw new Error(`Bundled CLI must be a non-empty file: ${cli}`)
      await NativeArch.verify(cli, arch)
      const version = path.join(path.dirname(cli), "spectuner-server.version")

      if ((await stat(version)).size === 0) throw new Error(`Bundled CLI version must be a non-empty file: ${version}`)

      // Started as programs off Windows, so they must be executable there.
      if (target.os !== "win32") await requireExecutable(cli)

      // SpecTuner's sidecar, tool plugin and modder prompt: the app's server cannot work without them.
      await Promise.all(
        spectunerResources(target.os).map(async (name) => {
          const file = path.join(resources, "spectuner", name)
          const info = await stat(file).catch(() => undefined)

          if (!info?.isFile() || info.size === 0)
            throw new Error(`Bundled SpecTuner resource must be a non-empty file: ${file}. Run script/stage-spectuner.ts`)
        }),
      )

      if (target.os !== "win32") {
        const sidecar = path.join(resources, "spectuner", SpecTunerEnv.sidecarFile(target.os as NodeJS.Platform))
        await requireExecutable(sidecar)
        // The sidecar is a .NET single-file program: check its native host, not the managed assemblies packed in it.
        await NativeArch.verifyHost(sidecar, target.os === "linux" ? "ELF" : "Mach-O", arch)
      }

      // Without it the window and the taskbar entry would fall back to the exe's embedded icon.
      await Promise.all(
        icons.map(async (name) => {
          const file = path.join(resources, "icons", name)
          const info = await stat(file).catch(() => undefined)

          if (!info?.isFile() || info.size === 0)
            throw new Error(`Bundled icon must be a non-empty file: ${file}. Run script/copy-icons.ts`)
        }),
      )
    },
    // Each system's blocks only in its own build, so the Windows configuration is exactly what it was.
    ...(target.os === "darwin"
      ? {
        mac: {
          category: "public.app-category.utilities",
          icon: "resources/icons/icon.icns",
          target: ["dmg", "zip"],
          // No signing identity: the whole bundle, the server and the sidecar inside it included, is signed ad hoc, so its
          // seal is intact and a player can open it with "Open Anyway". Ad-hoc signing with the hardened runtime would make
          // macOS reject Electron's own frameworks.
          identity: "-",
          hardenedRuntime: false,
          extendInfo: MAC_FOLDER_PROMPTS,
        },
        dmg: { icon: "resources/icons/icon.icns" },
        }
      : {}),
    ...(target.os === "linux"
      ? {
        linux: {
          // A folder of `<size>x<size>.png` files, installed into the icon theme under the executable's name.
          icon: "resources/icons",
          category: "Utility",
          // The executable must not end in `.desktop` (the prod app ID does): AppImage integration tools read every
          // `*.desktop` file at the image's root as a launcher, the executable included.
          executableName: linuxExecutableName(appId),
          // The launcher's file name comes from `desktopName` (`<appId>.desktop`), so the shell matches the running window
          // (whose class Electron takes from it) to the launcher and its icon.
          syncDesktopName: true,
          // The deb needs a maintainer and a homepage (see homepage()). No e-mail address is published.
          maintainer: "SpecTuner",
          synopsis: "Tune and change your Unity games",
          description: "SpecTuner changes Unity games for the players who own them, from requests in plain language.",
          desktop: { entry: { StartupWMClass: appId } },
          target: ["AppImage", "deb"],
        },
        // The package name players remove it by (`apt remove spectuner`), one per channel so they install side by side.
        deb: { packageName: linuxPackageName(appId) },
        }
      : {}),
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
    ...getBase(APP_IDS[channel]),
    appId: APP_IDS[channel],
    productName: PRODUCT_NAMES[channel],
    // No update feed: upstream's would offer OpenCode as an update, and SpecTuner does not update itself.
    publish: null,
  }
}

// The deb's homepage field, which electron-builder requires: SPECTUNER_HOMEPAGE, else the repository a GitHub Actions
// run builds (GITHUB_SERVER_URL/GITHUB_REPOSITORY). A local Linux build without either fails with electron-builder's
// "Please specify project homepage" message.
function homepage(): Record<string, string> {
  const env = process.env
  const url =
    env.SPECTUNER_HOMEPAGE ||
    (env.GITHUB_SERVER_URL && env.GITHUB_REPOSITORY ? `${env.GITHUB_SERVER_URL}/${env.GITHUB_REPOSITORY}` : undefined)

  return url ? { homepage: url } : {}
}

function metadata(extra: Record<string, string>) {
  const all = { ...extra, ...(process.env.SPECTUNER_VERSION ? { version: process.env.SPECTUNER_VERSION } : {}) }

  return Object.keys(all).length ? all : undefined
}

async function requireExecutable(file: string) {
  const info = await stat(file).catch(() => undefined)

  if (!info?.isFile() || info.size === 0 || (info.mode & 0o111) === 0)
    throw new Error(`Bundled program must be a non-empty executable file: ${file}`)
}

export default getConfig()
