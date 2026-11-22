import { useLanguage } from "@/runtime/i18n/language"
import { formatServerError } from "@/runtime/server/errors"
import { ServerConnection } from "@/runtime/server/registry"
import { useGlobal } from "@/runtime/server/runtime"
import { showToast } from "@/shell/notifications/toast"
import { createProjectSessionActions } from "@/shell/tabs/project-sessions"
import { useDirectoryPicker } from "@/workspaces/selection/picker"

// SpecTuner. Choosing a game is picking its folder: the folder is added like a project, and a new
// chat opens on that exact folder (not on a git repository that happens to contain it), so the modder works on it.

/** Opens the folder picker; a picked folder opens a new chat for it. Cancelling does nothing. */
export function useChooseGame() {
  const language = useLanguage()
  const global = useGlobal()
  const picker = useDirectoryPicker()
  const sessions = createProjectSessionActions()

  return (conn: ServerConnection.Any) =>
    picker({
      server: conn,
      title: language.t("spectuner.chooseGame.title"),
      onSelect(result) {
        const directory = Array.isArray(result) ? result[0] : result
        const ctx = global.serverCtx(ServerConnection.key(conn))

        if (!directory || !ctx) return
        void ctx.projects.add(directory).then(
          () => sessions.openProjectNewSession(conn, directory),
          (error) =>
            showToast({
              variant: "error",
              title: language.t("spectuner.chooseGame.failed"),
              description: formatServerError(error, language.t),
            }),
        )
      },
    })
}
