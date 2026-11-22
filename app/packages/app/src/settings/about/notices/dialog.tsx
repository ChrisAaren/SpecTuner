import { For } from "solid-js"
import { Dialog, DialogBody, DialogHeader, DialogTitleGroup } from "@spectuner/ui/dialog"
import { useLanguage } from "@/runtime/i18n/language"
import { notices } from "./notices"

export default function DialogThirdPartyNotices() {
  const language = useLanguage()

  return (
    <Dialog size="x-large">
      <DialogHeader>
        <DialogTitleGroup
          title={language.t("settings.about.notices.title")}
          description={language.t("settings.about.notices.description")}
        />
      </DialogHeader>
      <DialogBody>
        <div class="settings-notices-scroll" data-scrollable>
          <div class="settings-notices">
            <For each={notices}>
              {(entry) => (
                <section class="settings-notice">
                  <div class="settings-notice-heading">
                    <span>{entry.name}</span>
                    <span>{entry.license}</span>
                  </div>
                  <p>{language.t(entry.detailKey)}</p>
                  <pre>{entry.notice}</pre>
                  <details>
                    <summary>{language.t("settings.about.notices.license")}</summary>
                    <pre>{entry.text}</pre>
                  </details>
                </section>
              )}
            </For>
          </div>
        </div>
      </DialogBody>
    </Dialog>
  )
}
