import { type ComponentProps, type JSX } from "solid-js"

// SpecTuner's mark: a performance gauge whose lit arc runs up to a needle pivoting on a game-pad d-pad. The paths are
// copied from `src/assets/logo/logo-mark.svg` (the source is `branding/` at the repository root). Every part uses
// `currentColor`, so CSS `color` tints it; the unlit track is drawn at 35% opacity.
const LIT_ARC =
  "M8.83 54.44A30.24 30.24 0 0 1 52.62 12.88A4.2 4.2 0 0 1 46.89 19.03A21.84 21.84 0 0 0 15.27 49.04A4.2 4.2 0 0 1 8.83 54.44Z"

const TRACK =
  "M58.45 20.34A30.24 30.24 0 0 1 55.17 54.44A4.2 4.2 0 0 1 48.73 49.04A21.84 21.84 0 0 0 51.1 24.41A4.2 4.2 0 0 1 58.45 20.34Z"

const NEEDLE = "M32.61 29.64L50.69 18A1.89 1.89 0 0 1 53.02 20.97L37.35 35.71Z"

const DPAD =
  "M34.1 45.5A1.47 1.47 0 0 0 35.57 44.03L35.57 38.57L41.03 38.57A1.47 1.47 0 0 0 42.5 37.1L42.5 32.9A1.47 1.47 0 0 0 41.03 31.43L35.57 31.43L35.57 25.97A1.47 1.47 0 0 0 34.1 24.5L29.9 24.5A1.47 1.47 0 0 0 28.43 25.97L28.43 31.43L22.97 31.43A1.47 1.47 0 0 0 21.5 32.9L21.5 37.1A1.47 1.47 0 0 0 22.97 38.57L28.43 38.57L28.43 44.03A1.47 1.47 0 0 0 29.9 45.5ZM34.73 35A2.73 2.73 0 1 0 29.27 35A2.73 2.73 0 1 0 34.73 35Z"

/**
 * The mark's paths. `outline` strokes them with a normalized length (`pathLength` 1) instead of filling them, so a
 * stroke-dash animation can draw the mark.
 */
function MarkPaths(props: { outline?: boolean }) {
  const paint = (opacity?: number): JSX.PathSVGAttributes<SVGPathElement> =>
    props.outline
      ? { fill: "none", stroke: "currentColor", "stroke-width": 1, "stroke-opacity": opacity, pathLength: 1 }
      : { fill: "currentColor", "fill-opacity": opacity }

  return (
    <>
      <path d={LIT_ARC} {...paint()} />
      <path d={TRACK} {...paint(0.35)} />
      <path d={NEEDLE} {...paint()} />
      <path d={DPAD} fill-rule="evenodd" {...paint()} />
    </>
  )
}

/** The mark alone, small: for example an empty pane's placeholder. */
export const Mark = (props: { class?: string }) => {
  return (
    <svg
      data-component="logo-mark"
      classList={{ [props.class ?? ""]: !!props.class }}
      viewBox="0 0 64 64"
      fill="none"
      aria-hidden="true"
      xmlns="http://www.w3.org/2000/svg"
    >
      <MarkPaths />
    </svg>
  )
}

/** The mark on the loading splash. */
export const Splash = (props: Pick<ComponentProps<"svg">, "ref" | "class">) => {
  return (
    <svg
      ref={props.ref}
      data-component="logo-splash"
      classList={{ [props.class ?? ""]: !!props.class }}
      viewBox="0 0 64 64"
      fill="none"
      aria-hidden="true"
      xmlns="http://www.w3.org/2000/svg"
    >
      <MarkPaths />
    </svg>
  )
}

/**
 * The mark beside the name "SpecTuner", set as text in the UI font. Its width comes from `class`; the mark and the
 * name scale with that width (`logo.css`). The row sits in an inner element, so a display class such as `block` on
 * the lockup cannot break it.
 */
export function Lockup(props: { class?: string; outline?: boolean; style?: JSX.CSSProperties }) {
  return (
    <div
      data-component="logo-lockup"
      role="img"
      aria-label="SpecTuner"
      classList={{ [props.class ?? ""]: !!props.class }}
      style={props.style}
    >
      <div data-slot="logo-lockup-row">
        <svg
          data-slot="logo-lockup-mark"
          viewBox="0 0 64 64"
          fill="none"
          aria-hidden="true"
          xmlns="http://www.w3.org/2000/svg"
          classList={{ "overflow-visible [&_path]:[vector-effect:non-scaling-stroke]": props.outline }}
        >
          <MarkPaths outline={props.outline} />
        </svg>
        <span data-slot="logo-lockup-name" aria-hidden="true">
          SpecTuner
        </span>
      </div>
    </div>
  )
}

/** SpecTuner's logo: the mark and the name. Used on the error page. */
export const Logo = (props: { class?: string }) => <Lockup class={props.class} />
