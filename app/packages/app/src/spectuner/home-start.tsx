import { Button } from "@spectuner/ui/button"
import { useDialog } from "@spectuner/ui/context/dialog"
import { Icon } from "@spectuner/ui/icon"
import { createMemo, Show, type JSX } from "solid-js"
import { useProviders } from "@/providers/catalog/providers"
import { useLanguage } from "@/runtime/i18n/language"
import { ServerProvider } from "@/runtime/server/current"
import { ServerConnection, useServers } from "@/runtime/server/registry"
import { useChooseGame } from "./choose-game"
import { SpecTunerLocalModelDialog } from "./local-model"

// SpecTuner. Home's empty state: the two things a player does before the first request, connect a
// model and choose the game's folder, each with the button that does it.

export function SpecTunerHomeStart() {
  const servers = useServers()
  const server = createMemo(() => servers.list.find(servers.current))

  // Home sits outside any server's context; the steps read the current server's default Location.
  return (
    <Show when={server()} keyed>
      {(conn) => (
        <ServerProvider conn={conn}>
          <Steps conn={conn} />
        </ServerProvider>
      )}
    </Show>
  )
}

function Steps(props: { conn: ServerConnection.Any }) {
  const language = useLanguage()
  const dialog = useDialog()
  const providers = useProviders(() => undefined)
  const chooseGame = useChooseGame()

  const connect = () =>
    void import("@/providers/connect/dialog").then(({ DialogConnectProvider }) => {
      void dialog.show(() => <DialogConnectProvider directory={undefined} defaultLocation />)
    })

  return (
    <div data-component="spectuner-home-start" class="flex min-h-full flex-col items-center gap-6 px-6 pt-[52px]">
      <div class="flex flex-col items-center gap-1 text-center">
        <h1 class="text-[15px] leading-5 tracking-[-0.04px] text-v2-text-text-base [font-weight:530]">
          {language.t("spectuner.start.title")}
        </h1>
        <p class="text-[13px] leading-5 tracking-[-0.04px] text-v2-text-text-muted [font-weight:440]">
          {language.t("spectuner.start.description")}
        </p>
      </div>
      <ol class="flex w-full max-w-[460px] flex-col gap-3">
        <Step
          number={1}
          title={language.t("spectuner.start.model.title")}
          description={language.t("spectuner.start.model.description")}
          done={providers.anyConnection() === true}
          doneLabel={language.t("spectuner.start.model.done")}
          action={
            <div class="flex flex-wrap gap-2">
              <Button data-action="spectuner-connect-model" variant="neutral" size="normal" onClick={connect}>
                {language.t("spectuner.start.model.action")}
              </Button>
              <Button
                data-action="spectuner-local-model"
                variant="ghost"
                size="normal"
                onClick={() => void dialog.show(() => <SpecTunerLocalModelDialog />)}
              >
                {language.t("spectuner.start.model.local")}
              </Button>
            </div>
          }
        />
        <Step
          number={2}
          title={language.t("spectuner.start.game.title")}
          description={language.t("spectuner.start.game.description")}
          done={false}
          action={
            <Button
              data-action="spectuner-choose-game"
              variant="neutral"
              size="normal"
              icon="folder"
              onClick={() => chooseGame(props.conn)}
            >
              {language.t("spectuner.start.game.action")}
            </Button>
          }
        />
      </ol>
    </div>
  )
}

function Step(props: {
  number: number
  title: string
  description: string
  done: boolean
  doneLabel?: string
  action: JSX.Element
}) {
  return (
    <li class="flex items-start gap-3 rounded-[10px] border border-v2-border-border-muted bg-v2-background-bg-layer-01 p-4">
      <span
        aria-hidden="true"
        class="flex size-6 shrink-0 items-center justify-center rounded-full bg-v2-background-bg-layer-02 text-[13px] leading-4 text-v2-text-text-muted [font-weight:530]"
      >
        <Show when={props.done} fallback={props.number}>
          <Icon name="check" class="text-v2-text-text-accent" />
        </Show>
      </span>
      <div class="flex min-w-0 flex-1 flex-col gap-1">
        <span class="text-[13px] leading-5 tracking-[-0.04px] text-v2-text-text-base [font-weight:530]">
          {props.title}
        </span>
        <span class="text-[13px] leading-5 tracking-[-0.04px] text-v2-text-text-muted [font-weight:440]">
          {props.description}
        </span>
        <div class="pt-2">
          <Show when={props.done && props.doneLabel} fallback={props.action}>
            {(label) => (
              <span class="text-[13px] leading-5 tracking-[-0.04px] text-v2-text-text-muted [font-weight:440]">
                {label()}
              </span>
            )}
          </Show>
        </div>
      </div>
    </li>
  )
}
