import type { SettingsView } from "@/settings/route"
import { SpecTunerProfile } from "./profile"

// SpecTuner. Upstream's settings routes stay as they are; this narrows the parsed view to the pages
// a player gets, so a link, command or plugin that names a hidden page (projects, servers, extensions, ...) opens
// Preferences instead.

/** The view to show for a parsed settings route: itself when the player gets that page, else Preferences. */
export function spectunerSettingsView(view: SettingsView): SettingsView {
  if (view.type === "root" && SpecTunerProfile.settingsPage(view.tab)) return view

  return { type: "root", tab: "general", searchActivation: view.searchActivation }
}

/**
 * The settings search results a player can open: none on a page SpecTuner leaves out (with its projects and servers)
 * or on a row it hides.
 */
export function spectunerSettingsSearch<Item extends { view: SettingsView }>(items: Item[]) {
  return items.filter(
    (item) =>
      item.view.type === "root" &&
      SpecTunerProfile.settingsPage(item.view.tab) &&
      (!item.view.target || SpecTunerProfile.settingsRow(item.view.target)),
  )
}
