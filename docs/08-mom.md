# 08 - PiSharp.Mom

## 目标

Phase 08 实现 `PiSharp.Mom` 的最小可用 Slack bot runtime，把 pi-mono `mom` 里最核心的闭环先接起来：

1. Slack Socket Mode 收事件、ack envelope、处理 `@mention`、DM 和普通 channel chatter 日志
2. 每个 channel/DM 一个独立 workspace 目录
3. 每个 channel/DM 一个持久化 `SessionManager` 会话
4. 直接复用 `PiSharp.CodingAgent` 的工具与 Agent runtime 回答 Slack 消息
5. `workspace/events/*.json` 触发 immediate / one-shot / periodic synthetic turn
6. `PiSharp.Cli` 通过 `mom` 命名空间转发到 `PiSharp.Mom`
7. Slack 附件下载到 `attachments/`，并把本地路径同步进 agent context
8. 启动时 backfill 已有 channel 的历史消息，并通过 `attach` 把本地文件发回 Slack

这一阶段明确是“最小可跑”而不是一次性追平 TS 版全部能力。现在已经能跑真实 Slack bot 主链路，也会把 Slack 附件下载到 channel workspace、把本地路径带进上下文，在启动时回补已有 channel 的缺失历史，把基本 tool 进度发到 thread，并对 assistant 文本做主消息 streaming；Slack users/channels metadata 也已经会在启动时全量加载，并在 live turn 里按 TTL 或缺失项刷新，但更完整的 streaming 交互和 Docker sandbox 仍然留到后续。

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
- `MomSessionSync`
- `MomSystemPrompt`
- `MomTurnProcessor`
- `MomWorkspaceRuntime`
- `MomEventsWatcher`
- `MomLogBackfiller`
- `MomSlackTools`
- `MomThreadReporter`
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

- `<workspace>/runtime-stats.json`
- `<workspace>/<channel>/log.jsonl`
- `<workspace>/<channel>/MEMORY.md`
- `<workspace>/<channel>/scratch/`
- `<workspace>/<channel>/.pi-sharp/sessions/*.jsonl`

phase 08 现在也提供了一个只读入口：`pisharp mom stats <workspace>`（或 `PiSharp.Mom` 直接 `stats <workspace>`），把 `runtime-stats.json` 展开成人类可读的摘要，方便现场查看最近一次 backfill / reconnect 成功或失败，而不用手翻 JSON；如果要机器读，也支持 `pisharp mom stats --json <workspace>`。

和 TS 版不同的是，这里没有额外引入独立的 `context.jsonl` schema，而是直接复用现有 `SessionManager`：

- `log.jsonl` 继续做人类可读、可 grep 的消息日志
- `SessionManager` 继续做 LLM 真正消费的上下文持久化

这样 phase 08 不需要再在 `PiSharp.Mom` 里维护第二套消息格式或 compaction 管理器。

### 3. 直接复用 CodingAgentSession，而不是再造一套 mom agent

`MomTurnProcessor` 每次处理 Slack 消息时会：

1. 找到当前 channel 最新 session
2. 用 `MomSessionSync` 把 `log.jsonl` 里尚未进入 session 的普通用户消息同步进 `SessionManager`
3. 用 `CodingAgentRuntimeBootstrap` 解析 provider / model / api key
4. 构建 Slack 专用 system prompt
5. 用 `CodingAgentSession.CreateAsync()` 复原上下文并执行这次 prompt
6. 把新增消息 append 回 `SessionManager`

这意味着 `PiSharp.Mom` 天然继承了：

- 已有的 provider bootstrap
- 已有的 built-in tools
- 已有的 Agent tool-calling/runtime
- 已有的 session JSONL 格式

本阶段没有再额外实现更完整的 threaded streaming、custom skill loader 等 mom 特有增强，而是优先把复用路径打通。

### 4. 普通 channel chatter 先写 log，再在下次 turn 前同步进 session

TS 版 mom 的关键行为不是“只看触发它的那条消息”，而是：

- 在 bot 所在 channel 里把普通用户消息先写到 `log.jsonl`
- 真正被 @mention 时，再把这些未处理消息同步进 agent 上下文

C# 版现在也按这个模型工作：

- `SlackSocketModeClient` 会把普通 `message` 事件解析成 `RequiresResponse = false`
- `MomWorkspaceRuntime` 先统一写 `log.jsonl`
- 下一次真正触发 turn 时，`MomSessionSync` 会把缺失的用户消息 append 进 `SessionManager`

这样一来，mom 在 channel 中被唤醒时，不再只知道“最后一句 @mention”，而是能看到从上次运行后累积下来的 chatter。

### 5. Slack 附件先下载到本地，再把路径喂给 agent

TS 版 mom 会把用户上传的文件落到 workspace，再让 agent 用路径处理。C# 版这一步现在也补上了：

- `SlackSocketModeClient` 会接受 `file_share` 消息和带 `files` 的 mention / DM
- `MomChannelStore` 会把附件下载到 `<channel>/attachments/`
- `log.jsonl` 记录原始文件名和本地相对路径
- 当前 turn 和后续 `MomSessionSync` 都会把这些路径渲染到 `<slack_attachments>` block

这意味着 agent 至少已经能用现有的 `read` / `bash` 工具处理文本类附件或进一步操作本地文件。和 TS 版相比，目前还没有：

- 把图片附件直接作为 multimodal image content 送进模型
- 后台 backfill 附件补抓取

### 6. 启动时先做 log backfill，再接 live socket events

只靠 live Socket Mode 有一个明显问题：如果 mom 重启，期间发生的 channel chatter、DM 或 bot 自己的历史回复就会在本地 `log.jsonl` 出现断层。C# 版现在分成两段 backfill：

- 只扫描已经存在 `log.jsonl` 的 channel/DM 目录
- 通过 `conversations.history` 拉取比本地最新 timestamp 更新的消息
- 记录普通用户消息、`file_share` 附件消息以及 bot 自己的消息
- 跳过其他 bot 和不支持的 subtype
- 对第一次在本地见到的 channel/DM，再按当前 live 消息 timestamp 做一次 bounded backfill，只抓最近一小段缺失历史
- 如果 Socket Mode 连接重建，后续某个 channel 的首条新消息会按“本地最新 ts -> 当前消息 ts”再做一次 bounded gap backfill

这样做的直接结果是：

- `log.jsonl` 和真实 Slack 历史不容易因为重启而断档
- 下一次 mention 前，`MomSessionSync` 也能把这些补回来的消息继续同步进 session
- 启动后如果 socket replay 旧消息，runtime 会先按 timestamp cutoff 降级成“只记 log，不触发 turn”，再由 store 的 timestamp 去重兜住重复投递
- 新加入的 channel / DM 就算本地还没有目录，也能在第一次 live turn 前补上一小段近期上下文
- 长连接掉线期间漏掉的 chatter，也能在 reconnect 后按 channel 渐进补回本地上下文
- 这些 bootstrap / reconnect backfill 现在会直接写到 runtime 输出里，并附带累计 stats 摘要；统计本身也会持久化到 `<workspace>/runtime-stats.json`
- `runtime-stats.json` 除了累计计数，也会记录最近一次 startup backfill、reconnect、bootstrap/reconnect-gap backfill 成功，以及最近一次 bootstrap/reconnect-gap backfill failure 的时间、channel、单行原因摘要和结构化 failure kind，方便重启后继续排障

这一步现在已经补上 users/channels 元数据加载：启动时先拉一份全量 snapshot，让 backfill、`log.jsonl`、system prompt 和 session sync 一开始就尽量使用用户名 / channel 名，而不是裸 Slack ID。

### 7. `attach` 作为 mom 特有扩展工具接进 CodingAgentSession

TS 版 mom 不只是读附件，也能把本地生成的文件再发回 Slack。C# 版现在用 `CodingAgentSession` 的 extension 机制加了一个 mom 专用 `attach` tool：

- 工具只允许上传 workspace 根目录内的文件
- 相对路径默认按当前 channel directory 解析
- 通过 Slack `files.uploadV2` 把文件发回当前 channel
- system prompt 会把 `attach` 暴露给 agent，适合分享报告、截图、patch、生成物

这一步故意没有把 `attach` 做成全局 built-in tool，而是保持为 mom runtime 的专用能力，避免污染通用 coding-agent 场景。

### 8. thread 里先展示基础 tool 进度

TS 版 mom 会把工具过程放到 thread 里，避免主消息被低层细节刷屏。C# 版现在先接了一个收敛版：

- 每个 tool call 在 thread 中最多占用一条消息
- `ToolExecutionStarted` 时发 thread 消息
- `ToolExecutionCompleted` 时更新同一条消息为 done / failed 和结果摘要
- thread 报告是 best-effort，Slack thread 出错不会影响主 turn

这样 phase 08 已经具备“主消息给最终回答，thread 给工具过程”的基本分层，但还没有：

- tool partial updates 的丰富展示
- 多层 thread UI 或更结构化的卡片输出

### 9. Slack metadata 用共享 index + 按需刷新

TS 版 mom 会长期持有 workspace 级 Slack metadata。C# 版现在也有一个共享的 `MomSlackWorkspaceIndex`，但实现仍然故意保持简单：

- 启动时先通过 `users.list` 和 `conversations.list` / `conversations.list?types=im` 拉一份全量 snapshot
- `MomChannelStore`、`MomSystemPrompt` 和 `MomSessionSync` 都共享同一个 index
- live Slack 事件进入 `MomWorkspaceRuntime` 时，如果 user/channel 不在 index 里，或者距离上次刷新超过 TTL，就重新拉一份 snapshot
- 刷新失败时退回 Slack ID，不阻塞当前 turn

这样做的直接收益是：

- 新加进来的 DM/channel 不需要重启 mom 才能拿到可读名字
- 用户改名、频道改名最终会在后续 turn 中自动回补
- 普通 chatter 首次落盘时，就尽量写入 `userName` / `displayName`

这里还没有做 TS 版那种更细粒度的 targeted refresh，例如：

- 只对单个未知 DM/user 做定向查询，而不是重拉整个 snapshot
- 把 Slack rename/profile change 事件作为单独的 cache invalidation 信号

### 10. Slack 响应策略先保持简单

当前响应链路刻意保持最小：

- 收到请求后先发一条 `_Thinking..._`
- assistant 有文本增量时，会逐步 `chat.update` 主消息
- turn 完成后再用 `chat.update` 覆盖为最终回答
- 如果当前 channel 已有任务在跑，则回 `_Already working. Send \`stop\` to cancel._`
- 如果用户发送 `stop`，则取消对应 channel 的运行中 turn

这里还没有复刻 TS 版的：

- `[SILENT]` 约定
- 更丰富的 tool partial update 渲染和多层 thread 交互

原因很简单：phase 08 先保证“能稳定回答 Slack 消息”，再迭代更复杂的交互表现。

### 11. 先只支持 host execution

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

### 12. events watcher 走本地文件系统，而不是额外服务

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
| `context.ts` | `MomSessionSync` |
| `events.ts` | `MomEventsWatcher` |
| `agent.ts` | `MomTurnProcessor` + `MomSystemPrompt` |
| `pi agent` 复用路径 | `CodingAgentRuntimeBootstrap` + `CodingAgentSession` |

## 当前取舍

- 还没有 Docker sandbox，工具运行在 host
- 主消息已经有基础 streaming，但还没有更精细的节流、富格式和中断态展示
- users/channels 元数据已经有共享缓存和运行时刷新，但还没有更细粒度的 targeted 拉取或事件驱动失效
- 没有单独实现 workspace settings / auth schema，先复用现有 `SettingsManager` / provider bootstrap

这些取舍的目标是把 phase 08 收敛到一条最短真实链路：Slack 事件 → coding-agent turn → session/log 持久化 → Slack 回复。

## 验证

测试覆盖：

- `MomApplication` 的参数解析和 namespaced help
- `SlackSocketModeClient` 对 mention / DM / 普通 message / `file_share` / bot-self event 的解析，以及 startup cutoff
- `SlackMrkdwnFormatter` 的 Markdown → mrkdwn 基础转换
- `MomTurnProcessor` 的 prompt 归一化、附件路径注入、tool thread 更新、main message streaming、`[SILENT]`、Slack 回复、`log.jsonl` 落盘、session 持久化
- `MomWorkspaceRuntime` 的普通 channel chatter / 附件记录与下一次 turn 同步、reconnect gap 补抓，以及未知 user/channel 触发的 metadata 刷新
- `MomLogBackfiller` 的启动回补、首见 channel bounded backfill、reconnect gap backfill 和附件下载
- `MomSlackMetadataService` 的 snapshot 刷新、缺失项刷新和 TTL 刷新
- `MomSlackTools` 的 `attach` 上传能力
- `MomEventsWatcher` 的 immediate 触发和过期 one-shot 清理
- `PiSharp.Cli` 到 `PiSharp.Mom` 的 `mom` 命名空间转发

这个阶段的结果是：`PiSharp.Mom` 已经是一个真实可运行的 Socket Mode Slack bot，只是还没有把 TS 版 mom 的高阶能力全部搬过来。
