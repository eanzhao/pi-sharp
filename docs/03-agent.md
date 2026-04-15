# 03 - PiSharp.Agent

## 目标

Phase 03 实现 `PiSharp.Agent`，提供一个最小但完整的 Agent 运行时闭环：

1. 基于 `IChatClient` 的多轮 agent loop
2. 基于 `AIFunction` 的工具注册与执行
3. 面向 UI/CLI 的事件流和状态包装器 `Agent`

它依赖 `PiSharp.Ai` 的两项基础能力：

- `StreamAdapter`：把 MEAI 的 `ChatResponseUpdate` 适配成细粒度 assistant 事件
- `ModelMetadata` / `ExtendedUsageDetails`：保存模型信息和 usage 元数据

## 参考实现

- `pi-mono/packages/agent/src/types.ts`
- `pi-mono/packages/agent/src/agent-loop.ts`
- `pi-mono/packages/agent/src/agent.ts`

## 本阶段范围

- `AgentEvent` 判别联合
- `AgentState` 状态容器
- `AgentTool` / `AgentToolResult`
- `AgentLoop.RunAsync()` / `ContinueAsync()`
- `Agent` 状态包装器、订阅器、steering/follow-up 队列
- xUnit 测试覆盖主链路、hook 和并行工具执行

## 设计

### 1. 消息模型

这一阶段先直接使用 MEAI 的 `ChatMessage` 作为 agent transcript 的消息类型，不额外引入
新的消息联合。原因有两个：

- phase 03 的主目标是先把 LLM 调用、工具执行和事件系统跑通
- `PiSharp.CodingAgent` 之前还没有真正的自定义 app 消息需求

为补齐 pi-mono 在 assistant/tool 消息上依赖的 stop reason、usage、tool metadata，
`PiSharp.Agent` 把这些运行时数据写入 `ChatMessage.AdditionalProperties`。

### 2. 低层 AgentLoop

`AgentLoop` 的职责和 TS 版一致：

- 接收 prompt 或 continuation
- 在每轮 assistant 响应前执行 `transformContext`
- 在真正发给模型前执行 `convertToLlm`
- 流式消费 `IChatClient.GetStreamingResponseAsync()`
- 将 assistant message 中的 tool calls 执行完，再决定是否继续下一轮

控制流保持和 pi-mono 一致：

- 外层 loop：处理 follow-up messages
- 内层 loop：处理 tool calls 和 steering messages

### 3. 工具系统

`AgentTool` 以 `AIFunction` 为核心：

- `Function`：提供 schema、name、description 和默认调用行为
- `Label`：供 UI 展示
- `PrepareArguments`：对 provider 给出的原始参数做轻量预处理
- `ExecuteAsync`：真正执行工具

默认情况下，`AgentTool` 会直接调用 `AIFunction.InvokeAsync()`，并把返回值转换成：

- `Value`：写回 `FunctionResultContent`
- `Content`：给 UI/日志看的可读内容
- `Details`：附加结构化信息

### 4. Hook 模型

保留和 pi-mono 对齐的两个扩展点：

- `BeforeToolCall`
- `AfterToolCall`

当前 C# 版本的取舍是：

- hook 收到的是 `AIFunctionArguments`
- 最终参数绑定与校验仍由 `AIFunction.InvokeAsync()` 完成

也就是说，phase 03 没有复刻 TS 版“先独立验证、再调用工具”的完整分层，而是把
MEAI 的参数绑定能力作为执行阶段的一部分使用。这个差异不会阻塞 CodingAgent，但后续若
需要更严格的预校验，可以在 `AgentTool` 之上再补一层 binder。

### 5. 状态包装器 Agent

`Agent` 是高层有状态封装，负责：

- 维护 transcript 和当前 streaming message
- 维护 pending tool call ids
- 提供 `PromptAsync()` / `ContinueAsync()` / `Abort()`
- 维护 steering / follow-up 消息队列
- 把低层 loop 事件规约到可订阅的运行时状态

设计上仍然遵循 pi-mono：

- `AgentLoop` 是无状态执行器
- `Agent` 是有状态 orchestration wrapper

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.Agent |
|---|---|
| `AgentEvent` union | `AgentEvent` 判别联合 |
| `AgentTool` | `AgentTool` + `AIFunction` |
| `runAgentLoop()` | `AgentLoop.RunAsync()` |
| `runAgentLoopContinue()` | `AgentLoop.ContinueAsync()` |
| `Agent` | `Agent` |

## 当前取舍

- 仅使用 `ChatMessage`，暂未引入可扩展 custom agent message 模型
- tool result 对 LLM 使用 `FunctionResultContent`，UI 细节放在消息 metadata 中
- 不实现 `proxy.ts` 对应的代理流函数，这部分留到更上层的接入阶段
- `beforeToolCall` 的参数“验证后语义”先近似为“预处理后语义”

## 验证

测试覆盖：

- prompt → assistant tool call → tool result → assistant follow-up 的完整链路
- `beforeToolCall` 阻断和 `afterToolCall` 覆盖
- 并行工具执行时“并发执行 + 源顺序提交”
- `Agent.ContinueAsync()` 对 steering queue 的处理
