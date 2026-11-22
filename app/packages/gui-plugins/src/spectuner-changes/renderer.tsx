import { lazy, onCleanup, Suspense } from "solid-js"
import { createKeyed, Panel, type PanelTab, type Plugin } from "@spectuner/plugin/gui"
import { onIdle } from "@spectuner/plugin/gui/builtin"
import type Changes from "./index"

/**
 * SpecTuner's Changes panel in the session's side region: one pinned tab, selected whenever nothing else is, and
 * opened the first time each session is shown in this window, so a player always sees what was changed.
 */
const setup: Plugin.Setup<typeof Changes> = (ctx) => {
  const ChangesPanel = lazy(() => import("./panel"))
  onCleanup(onIdle(() => void ChangesPanel.preload()))

  // One stable tab object: the strip never rebuilds its trigger.
  const tab: PanelTab = {
    id: "changes",
    get title() {
      return ctx.t("tab")
    },
    pinned: true,
    fallback: true,
    draggable: false,
    closable: false,
  }

  ctx.add(Panel, {
    id: "changes",
    region: "side",
    width: "narrow",
    list: () => [tab],
    render: (props) => (
      <Suspense>
        <ChangesPanel session={props.session} />
      </Suspense>
    ),
  })

  // Opens the side region once per session per window; the player may close it again.
  const shown = new Set<string>()
  createKeyed(
    () => ctx.sessions.current()?.key,
    (key) => {
      const session = ctx.sessions.current()

      if (!session || shown.has(key)) return
      shown.add(key)

      if (!ctx.layout.side.opened(session)) ctx.layout.side.toggle(session)
    },
  )
}

export default setup
