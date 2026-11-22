import { createMemo, createSignal, For, Match, onCleanup, Show, Switch, untrack } from "solid-js"
import { usePlugin, type MountedSession } from "@spectuner/plugin/gui"
import { SpecTunerRpc } from "@spectuner/modder/rpc"
import { Button } from "@spectuner/ui/button"
import { Icon } from "@spectuner/ui/icon"
import type Changes from "./index"
import {
  createChangesState,
  selectChangesView,
  transitionChanges,
  type ChangesEffect,
  type ChangesEvent,
  type ChangeView,
  type PastView,
} from "./machine"

/** " · " with a no-break space before the dot. */
const NBSP_DOT = " · "

/** How a call can fail: an RPC failure (`{ type, message? }`), or an Error when the connection drops. */
type CallFailure = Error | { readonly type: string; readonly message?: string }

function failure(error: CallFailure) {
  if (error.message) return error.message

  return "type" in error ? error.type : String(error)
}

/** The Changes panel for the routed session's game; a new session or folder starts a fresh panel. */
export default function ChangesPanel(props: { session: MountedSession }) {
  const key = createMemo(() => `${props.session.key}\n${props.session.directory}`)

  return (
    <Show when={key()} keyed>
      {(_key) => <ChangesBody session={props.session} directory={untrack(() => props.session.directory)} />}
    </Show>
  )
}

function ChangesBody(props: { session: MountedSession; directory: string }) {
  const ctx = usePlugin<typeof Changes>()
  const initial = createChangesState()
  const [state, setState] = createSignal(initial.state)
  const view = createMemo(() => selectChangesView(state()))

  const failed = createMemo(() => {
    const current = view()

    return current.phase === "failed" ? current : undefined
  })

  const ready = createMemo(() => {
    const current = view()

    return current.phase === "ready" ? current : undefined
  })

  const time = createMemo(
    () => new Intl.DateTimeFormat(ctx.locale.locale(), { dateStyle: "medium", timeStyle: "short" }),
  )

  // Ends every request and the event subscription when this panel goes away.
  const ended = new AbortController()
  onCleanup(() => ended.abort())

  // A restarted or re-authenticated server gets a new client under the same id, so read it on every call.
  const rpc = () => props.session.server.client.rpc(SpecTunerRpc.Definition)
  const options = () => ({ location: { directory: props.directory }, signal: ended.signal })

  const dispatch = (event: ChangesEvent) => {
    if (ended.signal.aborted) return

    const next = transitionChanges(untrack(state), event)

    if (next.state !== untrack(state)) setState(next.state)
    next.effects.forEach(run)
  }

  const run = (effect: ChangesEffect) => {
    const attempt = effect.attempt

    if (effect.type === "retry") {
      const timer = setTimeout(() => dispatch({ type: "retry.due", attempt }), effect.delay)

      return void ended.signal.addEventListener("abort", () => clearTimeout(timer), { once: true })
    }

    if (effect.type === "fetch")
      return void Promise.try(() => rpc().status({}, options())).then(
        (status) => dispatch({ type: "loaded", attempt, status }),
        (error) => dispatch({ type: "load.failed", attempt, message: failure(error) }),
      )

    const request =
      effect.type === "undo"
        ? () => rpc().undo({ changeId: effect.changeId }, options())
        : effect.type === "reapply"
          ? () => rpc().reapply({ changeId: effect.changeId }, options())
          : () => rpc().restore({}, options())

    void Promise.try(request).then(
      (result) => dispatch({ type: "action.done", attempt, result }),
      (error) => dispatch({ type: "action.failed", attempt, message: failure(error) }),
    )
  }

  // The server publishes `changed` after every game tool call, undo, restore and "Apply again" in this game's folder.
  const same = (a: string, b: string) => a.replaceAll("\\", "/").toLowerCase() === b.replaceAll("\\", "/").toLowerCase()
  rpc().events.on(
    "changed",
    (event) => {
      if (same(event.data.directory, props.directory)) dispatch({ type: "refresh" })
    },
    { signal: ended.signal },
  )
  initial.effects.forEach(run)

  const restore = async () => {
    const confirmed = await ctx.dialogs.confirm({
      title: ctx.t("restore.confirm.title"),
      description: ctx.t("restore.confirm.description"),
      confirm: ctx.t("restore"),
      danger: true,
    })

    if (confirmed) dispatch({ type: "restore" })
  }

  const when = (iso: string) => {
    const date = new Date(iso)

    return Number.isNaN(date.getTime()) ? "" : time().format(date)
  }

  // Muted lines like "Version 2 · Undone by you · 21 Nov 2026, 14:03". The dot stays with the words before it, so a
  // narrow panel wraps after it rather than starting a line with it.
  const line = (...parts: string[]) => parts.filter(Boolean).join(NBSP_DOT)

  const version = (item: { readonly version: number | undefined }) =>
    item.version === undefined ? "" : ctx.t("version", { version: item.version })

  // A current change: its version when there are several builds of its mod, and when it was made.
  const made = (change: ChangeView) => line(version(change), when(change.createdAt))

  // A past change: its version, what happened to it, and when.
  const outcome = (item: PastView) => line(version(item), ctx.t(`past.reason.${item.reason}`), when(item.endedAt))

  return (
    <div class="flex h-full min-h-0 flex-col bg-v2-background-bg-base" data-component="spectuner-changes">
      <div class="min-h-0 flex-1 overflow-y-auto px-4 py-3" data-scrollable>
        <Switch>
          <Match when={view().phase === "loading"}>
            <p class="text-[13px] leading-5 text-v2-text-text-muted">{ctx.t("loading")}</p>
          </Match>
          <Match when={failed()}>
            {(failed) => (
              <div class="flex flex-col gap-1 text-[13px] leading-5">
                <p class="text-v2-text-text-base">{ctx.t("failed")}</p>
                <p class="text-v2-text-text-muted">{failed().message}</p>
              </div>
            )}
          </Match>
          <Match when={ready()}>
            {(ready) => (
              <div class="flex flex-col gap-4">
                <div class="flex flex-col gap-1">
                  <h2 class="text-[15px] leading-5 text-v2-text-text-base [font-weight:530]">
                    {ready().game ?? ctx.t("game.unknown")}
                  </h2>
                  <p class="text-[13px] leading-5 text-v2-text-text-muted">
                    {ready().declined ?? ctx.t("game.supported")}
                  </p>
                </div>

                <Show when={ready().notice}>
                  {(notice) => (
                    <div
                      role="status"
                      data-slot="spectuner-changes-notice"
                      class="flex items-start gap-2 rounded-[8px] border border-v2-border-border-muted bg-v2-background-bg-layer-01 px-3 py-2 text-[13px] leading-5"
                      classList={{ "text-v2-text-text-base": notice().ok, "text-v2-state-fg-danger": !notice().ok }}
                    >
                      <span class="min-w-0 flex-1">{notice().message}</span>
                      <Button variant="ghost" size="small" onClick={() => dispatch({ type: "notice.dismiss" })}>
                        {ctx.t("dismiss")}
                      </Button>
                    </div>
                  )}
                </Show>

                <Show when={!ready().declined || ready().changes.length > 0 || ready().setup}>
                  <Show
                    when={ready().changes.length > 0}
                    fallback={
                      <p class="text-[13px] leading-5 text-v2-text-text-muted">
                        {ready().past.length > 0 ? ctx.t("empty.past") : ctx.t("empty")}
                      </p>
                    }
                  >
                    <ul class="flex flex-col gap-2" data-slot="spectuner-changes-list">
                      <For each={ready().changes}>
                        {(change) => (
                          <li
                            data-change={change.id}
                            class="flex items-start gap-3 rounded-[8px] border border-v2-border-border-muted bg-v2-background-bg-layer-01 px-3 py-2"
                          >
                            <div class="flex min-w-0 flex-1 flex-col gap-0.5">
                              <span class="text-[13px] leading-5 text-v2-text-text-base [font-weight:530]">
                                {change.title}
                              </span>
                              <span class="truncate text-[13px] leading-5 text-v2-text-text-muted">
                                {change.description}
                              </span>
                              <span class="text-[12px] leading-4 text-v2-text-text-faint">{made(change)}</span>
                            </div>
                            <Button
                              data-action="spectuner-undo"
                              variant="neutral"
                              size="small"
                              disabled={ready().busy}
                              onClick={() => dispatch({ type: "undo", changeId: change.id })}
                            >
                              {ready().undoing === change.id ? ctx.t("undoing") : ctx.t("undo")}
                            </Button>
                          </li>
                        )}
                      </For>
                    </ul>
                  </Show>

                  <Show when={ready().setup}>
                    <div class="flex flex-col gap-0.5 px-1 text-[12px] leading-4" data-slot="spectuner-changes-setup">
                      <span class="text-v2-text-text-muted">{ctx.t("setup")}</span>
                      <span class="text-v2-text-text-faint">{ctx.t("setup.description")}</span>
                    </div>
                  </Show>
                </Show>

                {/* Past changes stay listed after Restore game, when nothing else is. */}
                <Show when={ready().past.length > 0}>
                  <section class="flex flex-col gap-2" data-slot="spectuner-changes-past">
                    <button
                      type="button"
                      data-action="spectuner-past-toggle"
                      aria-expanded={ready().pastOpen}
                      class="flex w-full items-center gap-1 rounded-[4px] px-1 text-start text-[13px] leading-5 text-v2-text-text-base outline-none [font-weight:530] focus-visible:shadow-[0_0_0_1px_var(--v2-border-border-focus)]"
                      onClick={() => dispatch({ type: "past.toggle" })}
                    >
                      <Icon
                        name="chevron-right"
                        class="shrink-0 text-v2-icon-icon-faint"
                        classList={{ "rotate-90": ready().pastOpen, "rtl:-scale-x-100": !ready().pastOpen }}
                      />
                      <span class="min-w-0 flex-1">{ctx.t("past.title", { count: ready().pastTotal })}</span>
                    </button>

                    <Show when={ready().pastOpen}>
                      <ul class="flex flex-col gap-2" data-slot="spectuner-changes-past-list">
                        <For each={ready().past}>
                          {(item) => (
                            <li
                              data-past={item.id}
                              class="flex items-start gap-3 rounded-[8px] border border-v2-border-border-muted bg-v2-background-bg-layer-01 px-3 py-2"
                            >
                              <div class="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span class="text-[13px] leading-5 text-v2-text-text-base [font-weight:530]">
                                  {item.title}
                                </span>
                                <span class="truncate text-[13px] leading-5 text-v2-text-text-muted">
                                  {item.description}
                                </span>
                                <span class="text-[12px] leading-4 text-v2-text-text-faint">{outcome(item)}</span>
                                <Show when={!item.canReapply && item.whyNot}>
                                  {(whyNot) => (
                                    <span class="text-[12px] leading-4 text-v2-text-text-muted">{whyNot()}</span>
                                  )}
                                </Show>
                                <Show when={item.canReapply && item.kind === "mod"}>
                                  <span class="text-[12px] leading-4 text-v2-text-text-muted">{ctx.t("past.mod")}</span>
                                </Show>
                              </div>
                              <Show when={item.canReapply}>
                                <Button
                                  data-action="spectuner-reapply"
                                  variant="neutral"
                                  size="small"
                                  disabled={ready().busy}
                                  onClick={() => dispatch({ type: "reapply", changeId: item.id })}
                                >
                                  {ready().reapplying === item.id ? ctx.t("past.reapplying") : ctx.t("past.reapply")}
                                </Button>
                              </Show>
                            </li>
                          )}
                        </For>
                      </ul>
                      <Show when={ready().pastTruncated}>
                        <p class="px-1 text-[12px] leading-4 text-v2-text-text-muted">
                          {ctx.t("past.truncated", { count: ready().past.length })}
                        </p>
                      </Show>
                    </Show>
                  </section>
                </Show>
              </div>
            )}
          </Match>
        </Switch>
      </div>

      <Show when={ready()?.canRestore ? ready() : undefined}>
        {(ready) => (
          <div class="shrink-0 border-t border-v2-border-border-muted px-4 py-3">
            <Button
              data-action="spectuner-restore"
              variant="danger"
              size="normal"
              class="w-full"
              disabled={ready().busy}
              onClick={() => void restore()}
            >
              {ready().restoring ? ctx.t("restoring") : ctx.t("restore")}
            </Button>
          </div>
        )}
      </Show>
    </div>
  )
}
