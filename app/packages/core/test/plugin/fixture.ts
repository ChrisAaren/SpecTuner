import { Agent } from "@spectuner/core/agent"
import { App } from "@spectuner/core/app"
import { Artifact } from "@spectuner/core/artifact"
import { BlobStore } from "@spectuner/core/blob-store"
import { AISDK } from "@spectuner/core/aisdk"
import { Command } from "@spectuner/core/command"
import { Config } from "@spectuner/core/config"
import { Credential } from "@spectuner/core/credential"
import { Database } from "@spectuner/core/database/database"
import { Environment } from "@spectuner/core/environment/index"
import { FileAccess } from "@spectuner/core/file-access"
import { Flag } from "@spectuner/core/flag"
import { LayerNodePlatform } from "@spectuner/util/effect/app-node-platform"
import { AppProcess } from "@spectuner/util/process"
import { LayerNode } from "@spectuner/util/effect/layer-node"
import { Bus } from "@spectuner/core/bus"
import { FileSystem } from "@spectuner/core/filesystem"
import { FSUtil } from "@spectuner/util/fs-util"
import { Global } from "@spectuner/util/global"
import { Form } from "@spectuner/core/form"
import { Generate } from "@spectuner/core/generate"
import { InstructionDiscovery } from "@spectuner/core/instruction-discovery"
import { Integration } from "@spectuner/core/integration"
import { Job } from "@spectuner/core/job"
import { KV } from "@spectuner/core/kv"
import { Location } from "@spectuner/core/location"
import { Mcp } from "@spectuner/core/mcp/index"
import { Model } from "@spectuner/core/model"
import { Npm } from "@spectuner/util/npm"
import { Plugin } from "@spectuner/core/plugin"
import { PluginHooks } from "@spectuner/core/plugin/hooks"
import { Provider } from "@spectuner/core/provider"
import { Session } from "@spectuner/core/session"
import { SharedCache } from "@spectuner/core/shared-cache"
import { InstructionEntry } from "@spectuner/core/session/instruction-entry"
import { PersistentPty } from "@spectuner/core/persistent-pty"
import { LocationServiceMap } from "@spectuner/core/location-service-map"
import { AppNodeBuilder } from "@spectuner/core/effect/app-node-builder"
import { Permission } from "@spectuner/core/permission"
import { Reference } from "@spectuner/core/reference"
import { Rpc } from "@spectuner/core/rpc"
import { Skill } from "@spectuner/core/skill"
import { SkillDiscovery } from "@spectuner/core/skill/discovery"
import { Watcher } from "@spectuner/core/filesystem/watcher"
import { Tool } from "@spectuner/core/tool"
import { Vcs } from "@spectuner/core/vcs"
import { VideoGeneration } from "@spectuner/core/video-generation"
import { WebSearch } from "@spectuner/core/websearch"
import { Worktree } from "@spectuner/core/worktree"
import { WorktreeStrategies } from "@spectuner/core/worktree/strategies"
import { Effect, Layer } from "effect"
import { tempLocationLayer } from "../fixture/location"
import { emptyMcpLayer } from "../fixture/mcp"
import { offlineModels } from "../fixture/models"

const npmLayer = Layer.succeed(
  Npm.Service,
  Npm.Service.of({
    add: (name) => Effect.succeed({ directory: "", name }),
    resolve: (name) => Effect.succeed({ directory: "", name }),
    check: () => Effect.succeed(false),
    update: (name) => Effect.succeed({ directory: "", name }),
    follows: () => Effect.succeed(false),
    reject: () => Effect.succeed(false),
    which: () => Effect.undefined,
  }),
)

const generateLayer = Layer.succeed(
  Generate.Service,
  Generate.Service.of({
    text: () => Effect.succeed(""),
    image: () => Effect.die("unused"),
    decisions: () => Effect.die("unused"),
    video: {
      start: () => Effect.die("unused"),
      status: () => Effect.die("unused"),
      result: () => Effect.die("unused"),
      cancel: () => Effect.die("unused"),
    },
  }),
)

const configLayer = Config.testLayer()

const permissionLayer = Layer.succeed(
  Permission.Service,
  Permission.Service.of({
    close: Effect.void,
    ask: (input) => Effect.succeed({ id: input.id ?? Permission.ID.create(), effect: "ask" }),
    assert: () => Effect.void,
    assertNotDenied: () => Effect.void,
    allows: () => Effect.succeed(false),
    reply: () => Effect.void,
    get: () => Effect.succeed(undefined),
    forSession: () => Effect.succeed([]),
    list: () => Effect.succeed([]),
    nested: () => Effect.void,
  }),
)

export const makePluginTestLayer = (
  permission = permissionLayer,
  generate = generateLayer,
  replacements: LayerNode.Replacements = [],
) =>
  AppNodeBuilder.build(
    LayerNode.group([
      App.node,
      AppProcess.node,
      FileSystem.node,
      FSUtil.node,
      Location.node,
      Npm.node,
      Credential.node,
      Config.node,
      Flag.node,
      Database.node,
      Environment.node,
      FileAccess.node,
      Global.node,
      Bus.node,
      Form.node,
      Generate.node,
      LayerNodePlatform.httpClient,
      Plugin.node,
      Agent.node,
      Artifact.node,
      AISDK.node,
      BlobStore.node,
      Provider.node,
      Model.node,
      Command.node,
      InstructionDiscovery.node,
      Integration.node,
      Job.node,
      KV.node,
      Mcp.node,
      Session.node,
      SharedCache.node,
      InstructionEntry.node,
      PersistentPty.node,
      LocationServiceMap.node,
      Permission.node,
      PluginHooks.node,
      Reference.node,
      Rpc.node,
      Skill.node,
      SkillDiscovery.node,
      Tool.node,
      Vcs.node,
      VideoGeneration.node,
      Watcher.node,
      WebSearch.node,
      Worktree.node,
      WorktreeStrategies.node,
    ]),
    [
      Location.node.replace(tempLocationLayer),
      Npm.node.replace(npmLayer),
      Config.node.replace(configLayer),
      Mcp.node.replace(emptyMcpLayer),
      Generate.node.replace(generate),
      Permission.node.replace(permission),
      offlineModels,
      ...replacements,
    ],
  ).pipe(Layer.provideMerge(configLayer))

export const PluginTestLayer = makePluginTestLayer()
