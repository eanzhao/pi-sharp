# 08 - PiSharp.Mom

## 目标

Phase 08 实现 `PiSharp.Mom` 的最小可用 Slack bot runtime，把 pi-mono `mom` 里最核心的闭环先接起来：

1. Slack Socket Mode 收事件、ack envelope、处理 `@mention` 和 DM
2. 每个 channel/DM 一个独立 workspace 目录
3. 每个 channel/DM 一个持久化 `SessionManager` 会话
4. 直接复用 `PiSharp.CodingAgent` 的工具与 Agent runtime 回答 Slack 消息
5. `workspace/events/*.json` 触发 immediate / one-shot / periodic synthetic turn
6. `PiSharp.Cli` 通过 `mom` 命名空间转发到 `PiSharp.Mom`

这一阶段明确是“最小可跑”而不是一次性追平 TS 版全部能力。现在已经能跑真实 Slack bot 主链路，但刻意把附件下载、事件调度、Docker sandbox 和 thread 级工具详情留到后续。

## 参考实现

- `pi-mono/packages/mom/src/main.ts`
- `pi-mono/packages/mom/src/slack.ts`
- `pi-mono/packages/mom/src/store.ts`
- `pi-mono/packages/mom/src/agent.ts`
- `pi-mono/packages/mom/docs/slack-bot-minimal-guide.md`

## 本阶段范围

- `PiSharp.Mom.csproj`
- `MomApplication`
- `SlackWebApiClient`
- `SlackSocketModeClient`
- `MomChannelStore`
- `MomSystemPrompt`
- `MomTurnProcessor`
- `MomWorkspaceRuntime`
- `MomEventsWatcher`
- `PiSharp.Mom.Tests`
- `PiSharp.Cli` 的 `mom` 命名空间转发

## 设计

### 1. 先把 Slack transport 和 agent runtime 解耦

TS 版 `mom` 把 Slack、queue、context、agent orchestration 写在一起。C# 版先拆三层：

- `SlackWebApiClient`：负责 `auth.test`、`chat.postMessage`、`chat.update`、`chat.delete`
- `SlackSocketModeClient`：负责 `apps.connections.open` + WebSocket 循环 + envelope ack
- `MomWorkspaceRuntime` / `MomTurnProcessor`：负责把 Slack 事件翻译成一次 coding-agent turn

这样做有两个直接收益：

- Socket Mode 和 Web API 可以单独测试 parser / transport 逻辑
- 真正“做事”的部分只依赖 `ISlackMessagingClient`，不直接依赖 WebSocket 实现

### 2. 每个 Slack channel 对应一个本地工作目录

沿用 TS 版 mom 的基本思路：workspace 根目录下每个 channel 一个目录：

- `<workspace>/<channel>/log.jsonl`
- `<workspace>/<channel>/MEMORY.md`
- `<workspace>/<channel>/scratch/`
- `<workspace>/<channel>/.pi-sharp/sessions/*.jsonl`

和 TS 版不同的是，这里没有额外引入独立的 `context.jsonl` schema，而是直接复用现有 `SessionManager`：

- `log.jsonl` 继续做人类可读、可 grep 的消息日志
- `SessionManager` 继续做 LLM 真正消费的上下文持久化

这样 phase 08 不需要再在 `PiSharp.Mom` 里维护第二套消息格式或 compaction 管理器。

### 3. 直接复用 CodingAgentSession，而不是再造一套 mom agent

`MomTurnProcessor` 每次处理 Slack 消息时会：

1. 找到当前 channel 最新 session
2. 用 `CodingAgentRuntimeBootstrap` 解析 provider / model / api key
3. 构建 Slack 专用 system prompt
4. 用 `CodingAgentSession.CreateAsync()` 复原上下文并执行这次 prompt
5. 把新增消息 append 回 `SessionManager`

这意味着 `PiSharp.Mom` 天然继承了：

- 已有的 provider bootstrap
- 已有的 built-in tools
- 已有的 Agent tool-calling/runtime
- 已有的 session JSONL 格式

本阶段没有再额外实现 `attach`、threaded tool logs、custom skill loader 等 mom 特有增强，而是优先把复用路径打通。

### 4. Slack 响应策略先保持简单

当前响应链路刻意保持最小：

- 收到请求后先发一条 `_Thinking..._`
- turn 完成后用 `chat.update` 覆盖为最终回答
- 如果当前 channel 已有任务在跑，则回 `_Already working. Send \`stop\` to cancel._`
- 如果用户发送 `stop`，则取消对应 channel 的运行中 turn

这里还没有复刻 TS 版的：

- thread 中展示工具调用详情
- `[SILENT]` 约定
- 渐进式 streaming 更新主消息

原因很简单：phase 08 先保证“能稳定回答 Slack 消息”，再迭代更复杂的交互表现。

### 5. 先只支持 host execution

pi-mono `mom` 的重要特性是 Docker sandbox。C# 版这一步没有跟进 sandbox executor，而是先直接使用 `CodingAgentSession` 的现有 built-in tools，也就是 host 上的：

- `read`
- `bash`
- `edit`
- `write`
- `grep`
- `find`
- `ls`

这让 phase 08 能在不重写第二套工具运行时的前提下尽快交付真实可跑的 Slack bot。

代价也非常明确：

- 当前 bot 的 shell/file access 直接落在 host 上
- 还没有 `docker:<container>` 这种执行隔离
- 暂时不适合把它当成高信任边界之外的生产 bot

因此文档和实现都把这点当作显式 tradeoff，而不是隐藏行为。

### 6. events watcher 走本地文件系统，而不是额外服务

参考 TS 版 `events.ts`，C# 版补了 `MomEventsWatcher`：

- 监听 `<workspace>/events/*.json`
- `immediate`：发现后立即触发，成功入队后删除
- `one-shot`：按 `at` 定时，触发后删除
- `periodic`：用 `Cronos` 计算下次 occurrence，触发后继续调度

触发时会生成 synthetic Slack turn：

```text
[EVENT:daily-inbox.json:periodic:0 9 * * 1-5] Check inbox
```

并通过 `MomWorkspaceRuntime` 的 per-channel queue 执行。和用户消息不同，事件在 channel 忙时不会直接回 “Already working”，而是最多排队 5 个，超过后丢弃。

这一步的价值在于：phase 08 不需要先接额外 webhook server，就已经能支持 reminder、polling 和外部程序通过写文件唤醒 mom。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.Mom |
|---|---|
| `main.ts` | `MomApplication` + `MomWorkspaceRuntime` |
| `slack.ts` | `SlackWebApiClient` + `SlackSocketModeClient` |
| `store.ts` | `MomChannelStore` |
| `events.ts` | `MomEventsWatcher` |
| `agent.ts` | `MomTurnProcessor` + `MomSystemPrompt` |
| `pi agent` 复用路径 | `CodingAgentRuntimeBootstrap` + `CodingAgentSession` |

## 当前取舍

- 只处理 `app_mention` 和 DM，不记录普通 channel chatter
- 还没有附件下载与 `attach` 工具
- 还没有 Docker sandbox，工具运行在 host
- 还没有 thread 级工具详情或 streaming UI，只保留单消息占位再覆盖
- 没有单独实现 workspace settings / auth schema，先复用现有 `SettingsManager` / provider bootstrap

这些取舍的目标是把 phase 08 收敛到一条最短真实链路：Slack 事件 → coding-agent turn → session/log 持久化 → Slack 回复。

## 验证

测试覆盖：

- `MomApplication` 的参数解析和 namespaced help
- `SlackSocketModeClient` 对 mention / DM / bot-self event 的解析
- `SlackMrkdwnFormatter` 的 Markdown → mrkdwn 基础转换
- `MomTurnProcessor` 的 prompt 归一化、`[SILENT]`、Slack 回复、`log.jsonl` 落盘、session 持久化
- `MomEventsWatcher` 的 immediate 触发和过期 one-shot 清理
- `PiSharp.Cli` 到 `PiSharp.Mom` 的 `mom` 命名空间转发

这个阶段的结果是：`PiSharp.Mom` 已经是一个真实可运行的 Socket Mode Slack bot，只是还没有把 TS 版 mom 的高阶能力全部搬过来。
