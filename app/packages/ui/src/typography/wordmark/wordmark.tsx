import type { ComponentProps } from "solid-js"
import { Lockup } from "../../components/logo"

/**
 * SpecTuner's wordmark: the mark and the name "SpecTuner" in the UI font, as wide as `class` makes it. `muted` (the
 * default) shows it faintly, as a watermark. `outline` strokes the mark instead of filling it, so the first-launch
 * splash can draw it with a stroke-dash animation on its paths. `fade` is accepted for older callers and draws
 * nothing different.
 */
export function Wordmark(
  props: Pick<ComponentProps<"svg">, "class"> & { fade?: boolean; muted?: boolean; outline?: boolean },
) {
  return (
    <Lockup
      class={props.class}
      outline={props.outline}
      style={props.muted === false ? undefined : { opacity: 0.12 }}
    />
  )
}
