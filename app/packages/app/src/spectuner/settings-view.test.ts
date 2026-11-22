import { expect, test } from "bun:test"
import { dict } from "@spectuner/ui/i18n/en"
import en from "@/runtime/i18n/en"
import { parseSettingsView, type SettingsRootTab } from "@/settings/route"
import { settingsSearchIndex } from "@/settings/search-index"
import { SpecTunerProfile } from "./profile"
import { spectunerSettingsSearch, spectunerSettingsView } from "./settings-view"

// Every way into a page the player does not get lands on Preferences; the pages they do get open as linked.
const rows: { name: string; search: string; tab: SettingsRootTab }[] = [
  { name: "Preferences", search: "", tab: "general" },
  { name: "Appearance", search: "?tab=appearance", tab: "appearance" },
  { name: "Notifications", search: "?tab=notifications", tab: "notifications" },
  { name: "Providers", search: "?tab=providers", tab: "providers" },
  { name: "Models", search: "?tab=models", tab: "models" },
  { name: "About", search: "?tab=about", tab: "about" },
  { name: "Shortcuts", search: "?tab=shortcuts", tab: "general" },
  { name: "Extensions", search: "?tab=extensions&subtab=mcps", tab: "general" },
  { name: "the old Plugins page", search: "?tab=plugins", tab: "general" },
  { name: "Projects", search: "?tab=projects", tab: "general" },
  { name: "Workspaces", search: "?tab=workspaces", tab: "general" },
  { name: "Servers", search: "?tab=servers", tab: "general" },
  { name: "a server's old page", search: "?server=local", tab: "general" },
  { name: "Experimental", search: "?tab=experimental", tab: "general" },
  { name: "a project's page", search: "?server=local&project=%2Fgame&tab=general", tab: "general" },
]

test.each(rows)("a link to $name opens tab $tab", (row) => {
  const view = spectunerSettingsView(parseSettingsView(row.search))

  expect(view.type).toBe("root")
  expect(view.tab).toBe(row.tab)
})

test("a hidden page keeps the search reveal's activation but not its target", () => {
  const view = spectunerSettingsView(
    parseSettingsView("?tab=extensions", { target: "settings-plugin", searchActivation: 3 }),
  )

  expect(view).toEqual({ type: "root", tab: "general", searchActivation: 3 })
})

const strings = new Map(Object.entries({ ...dict, ...en }))

test("settings search offers only the pages and rows a player gets", () => {
  const results = spectunerSettingsSearch(
    settingsSearchIndex({
      servers: [
        {
          key: "local",
          name: "Local server",
          connected: true,
          projects: [{ id: "proj_game", name: "Game", worktree: "/games/game" }],
        },
      ],
      selected: "local",
      desktop: true,
      browser: true,
      mobile: false,
      translate: (key) => strings.get(key) ?? key,
    }),
  )

  const tabs = new Set(results.map((item) => item.view.tab))
  const targets = results.flatMap((item) => (item.view.target ? [item.view.target] : []))

  expect([...tabs].toSorted()).toEqual(["about", "appearance", "general", "models", "notifications", "providers"])
  expect(results.some((item) => item.view.type === "project")).toBe(false)
  expect(targets.filter((target) => SpecTunerProfile.hiddenSettingsRows.includes(target))).toEqual([])
  // The kept rows stay searchable. The app is English only, so there is no language row.
  expect(targets).not.toContain("settings-language")
  expect(targets).toContain("settings-theme")
})
