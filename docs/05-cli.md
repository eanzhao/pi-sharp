# 05 - PiSharp.Cli

## 目标

Phase 05 的目标是把前四个阶段的能力接成一个真实可用的命令行入口。最初只落了 print mode；随后补齐了
phase 05 follow-up 中缺失的三块能力：

1. 交互式 TTY 模式，基于 `PiSharp.Tui`
2. 持久化 session、resume、fork
3. 多 provider 支持，以及可复用的 runtime bootstrap

当前 `PiSharp.Cli` 已经支持：

- print mode 和 interactive mode 共用同一条 `CodingAgentSession` 运行路径
- OpenAI / Anthropic / Google 三个 provider
- `AGENTS.md` / `CLAUDE.md` 祖先目录扫描
- persisted session 默认落盘到 `.pi-sharp/sessions/`
- `--resume` / `--fork` / `--no-session` 生命周期控制

## 参考实现

- `pi-mono/packages/coding-agent/src/cli/args.ts`
- `pi-mono/packages/coding-agent/src/cli/initial-message.ts`
- `pi-mono/packages/coding-agent/src/cli/list-models.ts`
- `pi-mono/packages/coding-agent/src/core/resource-loader.ts`
- `pi-mono/packages/coding-agent/src/core/session-manager.ts`
- `pi-mono/packages/coding-agent/src/main.ts`

## 当前范围

- `CliArgumentsParser`（含 `--json` 机器可读输出模式）
- `CliApplication`（print / json / interactive 三种输出模式）
- `Program.cs`
- `CliInteractive*`
- `CodingAgentContextLoader`
- `CodingAgentRuntimeBootstrap`
- `SessionManager`
- DI 容器（`Microsoft.Extensions.DependencyInjection`）
- MEAI middleware pipeline（`ChatClientBuilder` + `UseLogging`）
- `PiSharp.Cli.Tests`

## 设计

### 1. CLI 只保留参数与前端壳

provider/model/api-key/context/session-dir 的解析不再耦合在 `Program.cs` 或 `CliApplication` 里。
这部分被抽到 `PiSharp.CodingAgent`：

- `CodingAgentContextLoader`
- `CodingAgentProviderCatalog`
- `CodingAgentRuntimeBootstrap`
- `SettingsManager`

这样后续新增 Web、Pods 或别的 frontend 时，可以复用同一套 provider/bootstrap 逻辑，而不是复制一份
CLI 版本的初始化代码。

### 2. Provider catalog 扩到三家，并支持远程模型发现

`CodingAgentProviderCatalog` 当前内置：

- `openai`
- `anthropic`
- `google`

每个 provider 都提供：

- `ProviderConfiguration`
- 已知 `ModelMetadata`
- env-based API key 解析
- `modelId + apiKey -> IChatClient` 的工厂

`--list-models` 现在会在有对应 API key 时直接调用 provider API 拉取实时模型列表，并把远程结果和静态
`ModelMetadata` 合并：

- OpenAI：`GET /v1/models`
- Anthropic：`GET /v1/models`
- Google：`GET /v1beta/models`

远程结果会缓存到 `~/.pi-sharp/cache/models/*.json`，TTL 为 5 分钟；当 API 不可用时，会优先回退到缓存，
再退回静态内置模型，避免 `--list-models` 因网络或 provider 故障直接失效。

CLI 只负责把参数传进去，不再关心具体 SDK 初始化。

### 3. 上下文文件加载保持 pi-mono 的祖先目录扫描

`CodingAgentContextLoader` 会从工作目录一路向上扫描祖先目录，并在每层目录按：

- `AGENTS.md`
- `CLAUDE.md`

的优先级选一个 context 文件，从外到内注入 system prompt。

这层逻辑被挪到 shared layer 后，CLI 和后续 frontend 都可以得到一致的 repo-context 行为。

### 4. 运行模式分成 print / interactive，但共享同一个 session

两种模式都走同一条 `CodingAgentSession` 路径：

- print mode：一次性提交 prompt，把 assistant 文本流到 stdout
- interactive mode：当 stdin/stdout 都是 TTY 且没有初始 prompt 时启动 `PiSharp.Tui`

interactive mode 的实现刻意保持最小：

- 一个 transcript 区域
- 一个输入框
- live assistant streaming
- tool started / updated / completed 状态
- 同一个 session 内反复提交 prompt

这满足 phase 05 follow-up 对交互能力的闭环要求，但没有试图一次性追平 pi-mono 的完整 TUI。

### 5. Session 默认持久化，`--no-session` 走 ephemeral

CLI 默认把 session 落到：

`<working-directory>/.pi-sharp/sessions/`

也可以用：

- `--session-dir <dir>`
- `settings.json` 里的 `sessionDir`

覆盖。若显式传 `--no-session`，则继续保留完全内存态运行路径。

### 6. Session lifecycle

当前支持三种路径：

- 新 session：默认创建新的 persisted session
- `--resume <id-or-path>`：在现有 session 上继续追加消息
- `--fork <id-or-path>`：从既有 session branch 复制上下文，并写入一个新的 session 文件

`latest` 也可以作为 `--resume` / `--fork` 的 selector 使用。

### 7. 持久化格式

session 使用 JSONL：

1. 第一行是 `session` header
2. 后续每行是一个 `SessionEntry`

header 记录：

- `id`
- `cwd`
- `parentSession`
- `providerId`
- `modelId`
- `thinkingLevel`
- `systemPrompt`
- `toolNames`

entry 当前支持：

- `message`
- `thinkingLevelChange`
- `modelChange`
- `compaction`
- `label`

`message` entry 会保存可恢复的 `ChatMessage` 形态，而不只是纯文本，因此 tool call / tool result 也能在
resume 和 fork 后继续作为上下文恢复。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp |
|---|---|
| `parseArgs()` | `CliArgumentsParser.Parse()` |
| `buildInitialMessage()` | `CliApplication.BuildInitialPromptAsync()` |
| `loadProjectContextFiles()` | `CodingAgentContextLoader.Load()` |
| `resolveAppMode()` | `CliApplication.RunAsync()` |
| `createSessionManager()` | `SessionManager` |
| `main.ts` runtime bootstrap | `CodingAgentRuntimeBootstrap` |

## 当前取舍

- interactive mode 只覆盖 transcript + input + live stream，不做完整 pane 系统
- session selector 先支持 `id` / `path` / `latest`，不做更复杂的 picker
- `@file` 仍按文本文件处理，不做图片输入

### JSON 输出模式

`--json` flag 将最终响应序列化为机器可读的 JSON 输出，包含：

- `role`：消息角色
- `content`：最终 assistant 回复文本
- `model`：使用的模型 ID
- `toolCalls`：所有 tool call 记录（id, name, arguments）
- `messageCount`：总消息数

适用于管道集成和自动化场景。

### DI 容器与 MEAI Middleware

在 `--verbose` 模式下，CLI 通过 `Microsoft.Extensions.DependencyInjection` 构建 `IServiceProvider`，
配置 `ILoggerFactory`，并通过 `ChatClientBuilder.UseLogging()` 将 MEAI 的日志 middleware 注入到
`IChatClient` pipeline 中。这为后续添加 OpenTelemetry、缓存等 middleware 提供了基础。

## 验证

测试覆盖：

- 参数解析和冲突 flag 校验（含 `--json`）
- shared bootstrap 的 provider/api-key/context/session-dir 解析
- print mode 的 context 注入和远程模型发现/缓存/fallback
- JSON 输出模式的结构化响应
- persisted session 的 resume / fork / invalid selector
- interactive mode 在 TTY 下的最小编排
- `SessionManager` 的 JSONL 持久化与 tool-message round-trip
- MEAI middleware pipeline 通过 verbose 模式激活
