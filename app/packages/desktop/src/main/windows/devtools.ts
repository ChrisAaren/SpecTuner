/**
 * Whether a SpecTuner window may open the browser developer tools. Only the unpackaged development run may: in a
 * packaged build DevTools would give the player (or anyone at the keyboard) every server request and the server
 * password, one fetch away. `webPreferences.devTools: false` turns off the menu item, the keyboard shortcuts and
 * `openDevTools()` alike. The remote debugging port has its own policy (diagnostics/options.ts).
 */
export function devToolsEnabled(packaged: boolean) {
  return !packaged
}
