import { useDialog } from "@spectuner/ui/context/dialog"
import { Mark } from "@spectuner/ui/logo"
import { useLanguage } from "@/runtime/i18n/language"
import { usePlatform } from "@/runtime/platform/platform"
import { showToast } from "@/shell/notifications/toast"
import { SpecTunerProfile } from "./profile"

// SpecTuner. Settings > About for SpecTuner: the product, its version, and the OpenCode attribution
// and third-party notices the MIT licence asks to keep. Upstream's page (OpenCode's credits, wordmark and website,
// and a contributor count fetched from GitHub) is not shown.

export function SpecTunerAbout() {
  const language = useLanguage()
  const platform = usePlatform()
  const dialog = useDialog()

  const showNotices = async () => {
    // The license texts load only when someone reads them.
    const loaded = await import("@/settings/about/notices/dialog").catch(() => undefined)

    if (!loaded) {
      showToast({ variant: "error", title: language.t("settings.about.notices.loadFailed") })

      return
    }

    void dialog.show(() => <loaded.default />)
  }

  return (
    <div class="settings-about-content">
      <div class="settings-about-intro">
        <Mark class="w-12 self-center" />
        <h1 class="text-[20px] leading-7 text-v2-text-text-base [font-weight:530]">{SpecTunerProfile.name}</h1>
        <p>
          {language.t("settings.about.version", {
            version: platform.version ?? language.t("settings.about.devVersion"),
          })}
        </p>
      </div>
      <div class="settings-about-details">
        <p>{language.t("spectuner.about.description")}</p>
        <p>{language.t("spectuner.about.builtOn")}</p>
        <p>{language.t("settings.about.trademark")}</p>
        <p>
          <button type="button" class="settings-about-link" onClick={() => void showNotices()}>
            {language.t("settings.about.notices.title")}
          </button>
        </p>
      </div>
    </div>
  )
}
