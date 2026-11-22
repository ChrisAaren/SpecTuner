interface ImportMetaEnv {
  /** Vite's `base`, the path the build is served under, such as `/spectuner/`; `/` at the root. */
  readonly BASE_URL: string
  readonly VITE_SPECTUNER_SERVER_MODE?: "none" | "origin"
  readonly VITE_SPECTUNER_SERVER_HOST: string
  readonly VITE_SPECTUNER_SERVER_PORT: string
  readonly VITE_SPECTUNER_CHANNEL?: "local" | "dev" | "beta" | "prod"
  /** The release of the CLI whose server embeds this build. Absent in the hosted web app. */
  readonly VITE_SPECTUNER_VERSION?: string

  readonly VITE_SENTRY_DSN?: string
  readonly VITE_SENTRY_ENVIRONMENT?: string
  readonly VITE_SENTRY_RELEASE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

declare module "*.png" {
  const src: string
  export default src
}

declare module "*.mp4" {
  const src: string
  export default src
}

export declare module "solid-js" {
  namespace JSX {
    interface Directives {
      sortable: true
    }

    // The Element Timing attribute, which Solid's DOM types do not list.
    interface ImgHTMLAttributes<T> {
      elementtiming?: string
    }
  }
}
