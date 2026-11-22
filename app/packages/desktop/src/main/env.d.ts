interface ImportMetaEnv {
  readonly SPECTUNER_CHANNEL: string
  readonly SPECTUNER_VERSION?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
