// TODO: Keep additional network capabilities inside Schema and Protocol as the client grows; /effect must never import
// Core or Server. Preserve these datatype exports so internal model reorganizations do not require caller migrations.
import type { Effect } from "effect"
import type { SpecTuner } from "./client.js"

export * from "./generated/index"
export { SpecTuner } from "./client.js"
export type {
  AgentApi,
  AppApi,
  CommandApi,
  ConfigApi,
  EventApi,
  IntegrationApi,
  ModelApi,
  PluginApi,
  ProviderApi,
  ReferenceApi,
  RpcApi,
  RpcClient,
  WebSearchApi,
  SessionApi,
  SkillApi,
} from "./api.js"
export { Agent } from "@spectuner/schema/agent"
export { Command } from "@spectuner/schema/command"
export { Config } from "@spectuner/schema/config"
export { Credential } from "@spectuner/schema/credential"
export { Event } from "@spectuner/schema/event"
export { EventLog } from "@spectuner/schema/event-log"
export { FileSystem } from "@spectuner/schema/filesystem"
export { Form } from "@spectuner/schema/form"
export { Integration } from "@spectuner/schema/integration"
export { Location } from "@spectuner/schema/location"
export { Model } from "@spectuner/schema/model"
export { Permission } from "@spectuner/schema/permission"
export { PermissionSaved } from "@spectuner/schema/permission-saved"
export { Project } from "@spectuner/schema/project"
export { Worktree } from "@spectuner/schema/worktree"
export { Vcs } from "@spectuner/schema/vcs"
export { Provider } from "@spectuner/schema/provider"
export { Pty } from "@spectuner/schema/pty"
export { Question } from "@spectuner/schema/question"
export { Reference } from "@spectuner/schema/reference"
export { WebSearch } from "@spectuner/schema/websearch"
export { AbsolutePath, RelativePath } from "@spectuner/schema/schema"
export { Session } from "@spectuner/schema/session"
export { SessionInbox } from "@spectuner/schema/session-inbox"
export { SessionMessage } from "@spectuner/schema/session-message"
export { Skill } from "@spectuner/schema/skill"
export { Prompt } from "@spectuner/schema/prompt"
export { PromptInput } from "@spectuner/schema/prompt-input"
export type { SpecTunerEvent } from "@spectuner/protocol/groups/event"
export type SpecTunerClient = Effect.Success<ReturnType<typeof SpecTuner.make>>
