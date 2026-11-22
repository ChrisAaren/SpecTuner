import { NodeFileSystem } from "@effect/platform-node"
import { compile, emitEffectImported, emitEffectShape, emitPromise, write } from "@spectuner/httpapi-codegen"
import {
  ClientApi,
  clientNamespace,
  deprecatedClientPaths,
  effectOmitEndpoints,
  groupNames,
  promiseOmitEndpoints,
} from "@spectuner/protocol/client"
import { Agent } from "@spectuner/schema/agent"
import { Artifact } from "@spectuner/schema/artifact"
import { Automation } from "@spectuner/schema/automation"
import { Command } from "@spectuner/schema/command"
import { Config } from "@spectuner/schema/config"
import { Credential } from "@spectuner/schema/credential"
import { Event } from "@spectuner/schema/event"
import { EventLog } from "@spectuner/schema/event-log"
import { FileDiff } from "@spectuner/schema/file-diff"
import { FileSystem } from "@spectuner/schema/filesystem"
import { Form } from "@spectuner/schema/form"
import { Generate } from "@spectuner/schema/generate"
import { Instruction } from "@spectuner/schema/instruction"
import { InstructionEntry } from "@spectuner/schema/instruction-entry"
import { Integration } from "@spectuner/schema/integration"
import { Job } from "@spectuner/schema/job"
import { Location } from "@spectuner/schema/location"
import { Mcp } from "@spectuner/schema/mcp"
import { Model } from "@spectuner/schema/model"
import { Money } from "@spectuner/schema/money"
import { Permission } from "@spectuner/schema/permission"
import { PermissionSaved } from "@spectuner/schema/permission-saved"
import { Plugin } from "@spectuner/schema/plugin"
import { Project } from "@spectuner/schema/project"
import { Worktree } from "@spectuner/schema/worktree"
import { AgentAttachment, FileAttachment, Prompt, PromptMention } from "@spectuner/schema/prompt"
import { PromptInput } from "@spectuner/schema/prompt-input"
import { Provider } from "@spectuner/schema/provider"
import { Pty } from "@spectuner/schema/pty"
import { PtyTicket } from "@spectuner/schema/pty-ticket"
import { Question } from "@spectuner/schema/question"
import { Reference } from "@spectuner/schema/reference"
import { AbsolutePath, RelativePath } from "@spectuner/schema/schema"
import { Session } from "@spectuner/schema/session"
import { SessionMessage } from "@spectuner/schema/session-message"
import { SessionInbox } from "@spectuner/schema/session-inbox"
import { Shell } from "@spectuner/schema/shell"
import { Skill } from "@spectuner/schema/skill"
import { Snapshot } from "@spectuner/schema/snapshot"
import { UsageLimits } from "@spectuner/schema/usage-limits"
import { Vcs } from "@spectuner/schema/vcs"
import { WebSearch } from "@spectuner/schema/websearch"
import { Workspace } from "@spectuner/schema/workspace"
import { Effect, Schema } from "effect"
import { fileURLToPath } from "url"

const promiseContract = compile(ClientApi, {
  groupNames,
  omitEndpoints: promiseOmitEndpoints,
  namespace: clientNamespace,
  aliases: deprecatedClientPaths,
})
const effectContract = compile(ClientApi, {
  groupNames,
  omitEndpoints: effectOmitEndpoints,
  namespace: clientNamespace,
  aliases: deprecatedClientPaths,
})
const effectTypeReferences = [
  ...namespaceTypes("Agent", "@spectuner/schema/agent", Agent),
  ...namespaceTypes("Artifact", "@spectuner/schema/artifact", Artifact),
  ...namespaceTypes("Automation", "@spectuner/schema/automation", Automation),
  ...namespaceTypes("Command", "@spectuner/schema/command", Command),
  ...namespaceTypes("Config", "@spectuner/schema/config", Config),
  ...namespaceTypes("Credential", "@spectuner/schema/credential", Credential),
  ...namespaceTypes("Event", "@spectuner/schema/event", Event),
  ...namespaceTypes("EventLog", "@spectuner/schema/event-log", EventLog),
  ...namespaceTypes("FileDiff", "@spectuner/schema/file-diff", FileDiff),
  ...namespaceTypes("FileSystem", "@spectuner/schema/filesystem", FileSystem),
  ...namespaceTypes("Form", "@spectuner/schema/form", Form),
  ...namespaceTypes("Generate", "@spectuner/schema/generate", Generate),
  ...namespaceTypes("Instruction", "@spectuner/schema/instruction", Instruction),
  ...namespaceTypes("InstructionEntry", "@spectuner/schema/instruction-entry", InstructionEntry),
  ...namespaceTypes("Integration", "@spectuner/schema/integration", Integration),
  ...namespaceTypes("Job", "@spectuner/schema/job", Job),
  typeReference("Location.PublicRef", "@spectuner/schema/location", Location.PublicRef),
  typeReference("Location.PublicInfo", "@spectuner/schema/location", Location.PublicInfo),
  ...namespaceTypes("Mcp", "@spectuner/schema/mcp", Mcp),
  ...namespaceTypes("Model", "@spectuner/schema/model", Model),
  ...namespaceTypes("Money", "@spectuner/schema/money", Money),
  ...namespaceTypes("Permission", "@spectuner/schema/permission", Permission),
  ...namespaceTypes("PermissionSaved", "@spectuner/schema/permission-saved", PermissionSaved),
  ...namespaceTypes("Plugin", "@spectuner/schema/plugin", Plugin),
  ...namespaceTypes("Project", "@spectuner/schema/project", Project),
  ...namespaceTypes("Worktree", "@spectuner/schema/worktree", Worktree),
  ...namespaceTypes("PromptInput", "@spectuner/schema/prompt-input", PromptInput),
  ...namespaceTypes("Provider", "@spectuner/schema/provider", Provider),
  ...namespaceTypes("Pty", "@spectuner/schema/pty", Pty),
  ...namespaceTypes("PtyTicket", "@spectuner/schema/pty-ticket", PtyTicket),
  ...namespaceTypes("Question", "@spectuner/schema/question", Question),
  ...namespaceTypes("Reference", "@spectuner/schema/reference", Reference),
  ...namespaceTypes("Session", "@spectuner/schema/session", Session),
  ...namespaceTypes("SessionMessage", "@spectuner/schema/session-message", SessionMessage),
  ...namespaceTypes("SessionInbox", "@spectuner/schema/session-inbox", SessionInbox),
  ...namespaceTypes("Shell", "@spectuner/schema/shell", Shell),
  ...namespaceTypes("Skill", "@spectuner/schema/skill", Skill),
  ...namespaceTypes("Snapshot", "@spectuner/schema/snapshot", Snapshot),
  ...namespaceTypes("UsageLimits", "@spectuner/schema/usage-limits", UsageLimits),
  ...namespaceTypes("Vcs", "@spectuner/schema/vcs", Vcs),
  ...namespaceTypes("WebSearch", "@spectuner/schema/websearch", WebSearch),
  ...namespaceTypes("Workspace", "@spectuner/schema/workspace", Workspace),
  typeReference("Prompt", "@spectuner/schema/prompt", Prompt),
  typeReference("PromptMention", "@spectuner/schema/prompt", PromptMention),
  typeReference("FileAttachment", "@spectuner/schema/prompt", FileAttachment),
  typeReference("AgentAttachment", "@spectuner/schema/prompt", AgentAttachment),
  typeReference("AbsolutePath", "@spectuner/schema/schema", AbsolutePath),
  typeReference("RelativePath", "@spectuner/schema/schema", RelativePath),
]

await Effect.runPromise(
  Effect.all(
    [
      write(
        emitPromise(promiseContract, {
          mutableOutputs: true,
        }),
        fileURLToPath(new URL("../src/promise/generated", import.meta.url)),
      ),
      write(
        emitEffectImported(effectContract, {
          module: "../../contract",
          api: "ClientApi",
          shapeModule: "../api/api.js",
        }),
        fileURLToPath(new URL("../src/effect/generated", import.meta.url)),
      ),
      write(
        emitEffectShape(effectContract, {
          typeReferences: effectTypeReferences,
          outputTypes: {
            "event.subscribe": {
              name: "SpecTunerEvent",
              import: 'import type { SpecTunerEvent } from "@spectuner/protocol/groups/event"',
            },
          },
        }),
        fileURLToPath(new URL("../src/effect/api", import.meta.url)),
      ),
    ],
    { concurrency: 3, discard: true },
  ).pipe(Effect.provide(NodeFileSystem.layer)),
)

function namespaceTypes(namespace: string, module: string, values: object) {
  return Object.entries(values).flatMap(([name, schema]) =>
    Schema.isSchema(schema) ? [typeReference(`${namespace}.${name}`, module, schema)] : [],
  )
}

function typeReference(name: string, module: string, schema: Schema.Top) {
  return {
    schema,
    name,
    import: `import type { ${name.split(".")[0]} } from ${JSON.stringify(module)}`,
  }
}
