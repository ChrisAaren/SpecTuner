import { expect, test } from "bun:test"
import { DESKTOP_MENU, desktopMenuVisible, type DesktopMenuPlatform } from "@/shell/commands/desktop-menu"

// The app menus a player sees on each platform: no terminal, developer tools, CLI installer or upstream links, while
// the ordinary entries stay.
test.each<DesktopMenuPlatform>(["windows", "macos"])(
  "the %s menus offer no coding entries or upstream links",
  (platform) => {
    const entries = DESKTOP_MENU.filter((menu) => desktopMenuVisible(menu, platform)).flatMap((menu) =>
      (menu.items ?? []).filter((entry) => entry.type === "item" && desktopMenuVisible(entry, platform)),
    )

    const keys = entries.flatMap((entry) =>
      entry.type === "item" ? [entry.command ?? entry.action ?? entry.role] : [],
    )

    expect(keys).not.toContain("terminal.toggle")
    expect(keys).not.toContain("view.toggleDevTools")
    expect(keys).not.toContain("app.installCli")
    expect(entries.filter((entry) => entry.type === "item" && entry.href)).toEqual([])
    expect(keys).toContain("settings.open")
    expect(keys).toContain("logs.export")
    expect(keys).toContain("sidebar.toggle")
  },
)
