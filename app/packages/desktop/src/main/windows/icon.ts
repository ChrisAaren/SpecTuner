/**
 * The window icon's file in `<resources>/icons`: the `.ico` on Windows (window and taskbar), a PNG on Linux, and none
 * on macOS, which draws windows with the app bundle's icon. Packaging ships the same list (`iconResources` in
 * electron-builder.config.ts); an unpackaged run reads the folder script/copy-icons.ts fills.
 */
export function windowIconFile(platform: NodeJS.Platform = process.platform) {
  if (platform === "win32") return "icon.ico"

  if (platform === "darwin") return undefined

  return "icon.png"
}
