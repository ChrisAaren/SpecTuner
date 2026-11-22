import { Button } from "@spectuner/ui/button"
import { useDialog } from "@spectuner/ui/context/dialog"
import { Dialog, DialogBody, DialogHeader, DialogTitle } from "@spectuner/ui/dialog"
import { Field } from "@spectuner/ui/field"
import { RadioGroup, RadioItem } from "@spectuner/ui/radio"
import { TextInput } from "@spectuner/ui/text-input"
import { SpecTunerRpc } from "@spectuner/modder/rpc"
import { createMemo, createResource, createSignal, For, onCleanup, Show, untrack } from "solid-js"
import type { ModelSelection } from "@/providers/models/selection"
import { useLanguage } from "@/runtime/i18n/language"
import { useServerSDK } from "@/runtime/server/client"
import { useData } from "@/runtime/server/current"
import { SettingsList } from "@/settings/list"
import { SettingsRow } from "@/settings/row"
import {
  ASSUMED_CONTEXT,
  createLocalModelState,
  selectLocalModelView,
  transitionLocalModel,
  type LocalModelEffect,
  type LocalModelEvent,
  type LocalModelFailure,
} from "./local-model-machine"

// SpecTuner. "Use a local model": the form (a dialog) and its row on Settings > Providers. The
// server finds the models and writes the provider entry (packages/modder/plugin/local-model.ts).

/** The model list refresh after saving waits this long for the server to reload its config file. */
const RELOAD_MS = 1500

const failureKey = {
  "invalid-address": "spectuner.local.failure.invalidAddress",
  unreachable: "spectuner.local.failure.unreachable",
  rejected: "spectuner.local.failure.rejected",
  "not-compatible": "spectuner.local.failure.notCompatible",
  "no-models": "spectuner.local.failure.noModels",
  "invalid-model": "spectuner.local.failure.invalidModel",
  unreadable: "spectuner.local.failure.unreadable",
  request: "spectuner.local.failure.request",
} as const satisfies Record<LocalModelFailure, string>

export function SpecTunerLocalModelDialog(props: { address?: string; selection?: ModelSelection }) {
  const language = useLanguage()
  const dialog = useDialog()
  const sdk = useServerSDK()
  const data = useData()
  const [state, setState] = createSignal(createLocalModelState(props.address))
  const view = createMemo(() => selectLocalModelView(state()))
  const numbers = createMemo(() => new Intl.NumberFormat(language.intl()))
  const ended = new AbortController()
  onCleanup(() => ended.abort())

  const rpc = () => sdk.api.rpc(SpecTunerRpc.LocalDefinition)

  const dispatch = (event: LocalModelEvent) => {
    if (ended.signal.aborted) return

    const next = transitionLocalModel(untrack(state), event)

    if (next.state !== untrack(state)) setState(next.state)
    next.effects.forEach(run)
  }

  const run = (effect: LocalModelEffect) => {
    if (effect.type === "use") {
      // The server reloads its config file by itself; refresh the lists once it has, then choose the model here.
      const timer = setTimeout(() => {
        data.location.provider.invalidate()
        data.location.model.invalidate()
        void Promise.all([data.location.provider.sync(), data.location.model.sync()]).then(() =>
          props.selection?.set({ providerID: SpecTunerRpc.LOCAL_PROVIDER, modelID: effect.model }),
        )
      }, RELOAD_MS)

      return void ended.signal.addEventListener("abort", () => clearTimeout(timer), { once: true })
    }

    const attempt = effect.attempt
    const options = { signal: ended.signal }

    if (effect.type === "find")
      return void Promise.try(() => rpc().find({ address: effect.address, key: effect.key }, options)).then(
        (result) => dispatch({ type: "found", attempt, result }),
        () => dispatch({ type: "request.failed", attempt }),
      )

    void Promise.try(() =>
      rpc().save({ address: effect.address, key: effect.key, model: effect.model, context: effect.context }, options),
    ).then(
      (result) => dispatch({ type: "saved", attempt, result }),
      () => dispatch({ type: "request.failed", attempt }),
    )
  }

  const window = (context: number | null) =>
    context
      ? language.t("spectuner.local.context.reported", { tokens: numbers().format(context) })
      : language.t("spectuner.local.context.assumed", { tokens: numbers().format(ASSUMED_CONTEXT) })

  return (
    <Dialog size="normal" fit>
      <DialogHeader closeLabel={language.t("common.close")}>
        <DialogTitle>{language.t("spectuner.local.title")}</DialogTitle>
      </DialogHeader>
      <DialogBody class="gap-4 px-5 pb-5">
        <Show
          when={view().saved}
          fallback={
            <form
              data-component="spectuner-local-model"
              class="flex flex-col gap-4"
              onSubmit={(event) => {
                event.preventDefault()
                dispatch({ type: view().canSave ? "save" : "find" })
              }}
            >
              <div class="flex flex-col gap-1 text-[13px] leading-5 text-v2-text-text-muted">
                <p>{language.t("spectuner.local.description")}</p>
                <p>{language.t("spectuner.local.discovery")}</p>
              </div>
              <Field invalid={view().failure === "invalid-address"}>
                <Field.Label>{language.t("spectuner.local.address")}</Field.Label>
                <TextInput
                  class="!w-full"
                  appearance="large"
                  type="url"
                  data-action="spectuner-local-address"
                  placeholder={language.t("spectuner.local.address.placeholder")}
                  value={state().address}
                  autocomplete="off"
                  spellcheck={false}
                  onInput={(event) => dispatch({ type: "address.changed", value: event.currentTarget.value })}
                />
              </Field>
              <Field invalid={view().failure === "rejected"}>
                <Field.Label>{language.t("spectuner.local.key")}</Field.Label>
                <Field.Prefix class="!-mt-1 !text-[13px] !leading-5">
                  {language.t("spectuner.local.key.description")}
                </Field.Prefix>
                <TextInput
                  class="!w-full"
                  appearance="large"
                  type="password"
                  data-action="spectuner-local-key"
                  value={state().key}
                  autocomplete="off"
                  onInput={(event) => dispatch({ type: "key.changed", value: event.currentTarget.value })}
                />
              </Field>

              <Show when={view().models.length > 0}>
                <RadioGroup
                  label={language.t("spectuner.local.model")}
                  value={view().selected}
                  onChange={(model) => dispatch({ type: "model.selected", model })}
                >
                  <For each={view().models}>
                    {(model) => <RadioItem value={model.id} label={model.id} description={window(model.context)} />}
                  </For>
                </RadioGroup>
              </Show>

              <Show when={view().small}>
                <p data-component="spectuner-local-small" class="text-[13px] leading-5 text-v2-text-text-muted">
                  {language.t("spectuner.local.context.small")}
                </p>
              </Show>

              <Show when={view().failure}>
                {(failure) => (
                  <p role="alert" class="text-[13px] leading-5 text-v2-state-fg-danger">
                    {language.t(failureKey[failure()])}
                  </p>
                )}
              </Show>

              <div class="flex justify-end gap-2">
                <Show
                  when={view().models.length > 0}
                  fallback={
                    <Button
                      type="submit"
                      variant="submit"
                      data-action="spectuner-local-find"
                      disabled={!view().canFind}
                    >
                      {language.t(view().finding ? "spectuner.local.finding" : "spectuner.local.find")}
                    </Button>
                  }
                >
                  <Button
                    type="button"
                    variant="neutral"
                    disabled={!view().canFind}
                    onClick={() => dispatch({ type: "find" })}
                  >
                    {language.t("spectuner.local.find")}
                  </Button>
                  <Button type="submit" variant="submit" data-action="spectuner-local-use" disabled={!view().canSave}>
                    {language.t(view().saving ? "spectuner.local.saving" : "spectuner.local.use")}
                  </Button>
                </Show>
              </div>
            </form>
          }
        >
          {(model) => (
            <div class="flex flex-col gap-4" data-component="spectuner-local-model-saved">
              <p class="text-[13px] leading-5 text-v2-text-text-base">
                {language.t("spectuner.local.saved", { model: model() })}
              </p>
              <div class="flex justify-end">
                <Button variant="submit" onClick={() => dialog.close()}>
                  {language.t("common.close")}
                </Button>
              </div>
            </div>
          )}
        </Show>
      </DialogBody>
    </Dialog>
  )
}

/** Settings > Providers: the saved local model, with Change and Remove, or a button to set one up. */
export function SpecTunerLocalModelSection() {
  const language = useLanguage()
  const dialog = useDialog()
  const sdk = useServerSDK()
  const data = useData()
  const rpc = () => sdk.api.rpc(SpecTunerRpc.LocalDefinition)

  const [saved, { refetch }] = createResource(() =>
    rpc()
      .get({})
      .catch(() => null),
  )

  const current = () => (saved.state === "ready" || saved.state === "refreshing" ? saved.latest : undefined)
  const [removing, setRemoving] = createSignal(false)

  const open = () =>
    void dialog.show(
      () => <SpecTunerLocalModelDialog address={current()?.address} />,
      () => void refetch(),
    )

  const remove = async () => {
    setRemoving(true)
    await rpc()
      .remove({})
      .catch(() => undefined)
    setRemoving(false)
    void refetch()
    setTimeout(() => {
      data.location.provider.invalidate()
      data.location.model.invalidate()
      void Promise.all([data.location.provider.sync(), data.location.model.sync()])
    }, RELOAD_MS)
  }

  return (
    <div class="settings-section" data-component="spectuner-local-model-section">
      <h3 class="settings-section-title">{language.t("spectuner.local.section")}</h3>
      <SettingsList>
        <SettingsRow
          title={language.t("spectuner.local.title")}
          description={
            current()
              ? language.t("spectuner.local.current", { model: current()!.model, address: current()!.address })
              : language.t("spectuner.local.none")
          }
        >
          <div class="flex gap-2">
            <Show when={current()}>
              <Button variant="neutral" size="small" disabled={removing()} onClick={() => void remove()}>
                {language.t("spectuner.local.remove")}
              </Button>
            </Show>
            <Button variant="neutral" size="small" data-action="spectuner-local-open" onClick={open}>
              {language.t(current() ? "spectuner.local.change" : "spectuner.local.setUp")}
            </Button>
          </div>
        </SettingsRow>
      </SettingsList>
    </div>
  )
}
