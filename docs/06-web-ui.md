# 06 - PiSharp.WebUi

## 目标

Phase 06 实现 `PiSharp.WebUi`，交付一个**可复用、可测试、可服务器渲染**的 Web UI 组件库，
把 `PiSharp.Agent` 的 transcript 与流式状态映射为浏览器里的聊天界面。

这一阶段先完成最小闭环：

1. 订阅 `Agent` 事件并投影成适合组件消费的 UI 状态
2. 用 Blazor/Razor 组件渲染 user / assistant / tool transcript
3. 提供一个基础输入区，能直接把 prompt 发回 `Agent`
4. 覆盖 HTML 渲染测试，确保组件在无浏览器环境下也能验证

## 参考实现

- `pi-mono/packages/web-ui/src/ChatPanel.ts`
- `pi-mono/packages/web-ui/src/components/AgentInterface.ts`
- `pi-mono/packages/web-ui/src/components/MessageList.ts`
- `pi-mono/packages/web-ui/src/components/Messages.ts`
- `pi-mono/packages/web-ui/src/components/ThinkingBlock.ts`
- `pi-mono/packages/web-ui/src/utils/format.ts`

## 本阶段范围

- `PiSharp.WebUi.csproj`
- `AgentViewModel`
- `ChatPanel`
- `AgentInterface`
- `MessageList`
- `UserMessageView`
- `AssistantMessageView`
- `ToolCallView`
- `ToolResultMessageView`
- `ThinkingBlock`
- `PiSharp.WebUi.Tests`

## 设计

### 1. 用 Razor Class Library 对齐 C# 生态

pi-mono 的 `web-ui` 基于 Web Components + Tailwind。到了 C# 版本，最自然的等价物是
Razor Class Library：

- 组件可直接被 Blazor Server / Web App / SSR 复用
- 可以在不启动浏览器的情况下，用 `HtmlRenderer` 做快照式测试
- 不需要为 phase 06 额外引入前端打包器、Node 运行时和第二套构建链

因此 `PiSharp.WebUi` 本阶段实现的是**组件库**，不是完整站点。

### 2. 保持对 Agent 的薄绑定

参考 `AgentInterface.ts`，Web UI 的核心不是重新实现 agent state，而是做一层订阅适配。

`AgentViewModel` 负责：

- 持有 `Agent`
- 订阅 `AgentEvent`
- 暴露 `Messages` / `StreamingMessage` / `PendingToolCalls` / `IsStreaming`
- 提供 `SendAsync()` 让组件直接转发用户输入

这样组件层只关心渲染和交互，不碰 agent loop 细节。

### 3. transcript 渲染按消息角色拆分

`MessageList` 参考 pi-mono 的消息列表实现，但先采用更符合当前 C# transcript 的拆分方式：

- `ChatRole.User` → `UserMessageView`
- `ChatRole.Assistant` → `AssistantMessageView`
- `ChatRole.Tool` → `ToolResultMessageView`
- `State.StreamingMessage` → 额外渲染一个 streaming assistant block

assistant 消息内部再按 `AIContent` 的顺序渲染：

- `TextContent`
- `TextReasoningContent`
- `FunctionCallContent`

这样既保留了流式生成和工具调用顺序，也不需要在 phase 06 就做复杂的消息归并。

### 4. Markdown 直接转 HTML，但关闭原始 HTML

消息正文和 thinking block 都通过 Markdig 渲染。

为了避免把任意原始 HTML 直接注入页面，pipeline 默认：

- 开启常见 markdown 扩展
- 禁用 markdown 中的 raw HTML

这让 phase 06 的默认行为更适合作为组件库基线，后面如果需要富文本扩展，再单独开放。

### 5. phase 06 明确不做的内容

这一步不追求把 TS 版 `web-ui` 一次性搬完。以下能力后续再补：

- 附件上传与预览
- session 持久化 / IndexedDB
- artifacts panel / sandbox runtime
- provider key 管理
- model selector / thinking selector
- 自定义消息 renderer registry

本阶段的目标只是把 agent 聊天主链路先在 Web 侧跑起来。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.WebUi |
|---|---|
| `ChatPanel` | `ChatPanel.razor` |
| `AgentInterface` | `AgentInterface.razor` |
| `MessageList` | `MessageList.razor` |
| `Messages.ts` | `UserMessageView` / `AssistantMessageView` / `ToolResultMessageView` |
| `ThinkingBlock` | `ThinkingBlock.razor` |
| `formatUsage()` | `WebUiFormatting.FormatUsage()` |

## 当前取舍

- 先支持 `PiSharp.Agent.Agent`，不直接包完整 `CodingAgentSession`
- 先用 SSR-friendly HTML 渲染，不做浏览器专属 API
- 工具结果先单独显示，不在 assistant bubble 内做复杂配对折叠
- 样式只提供基础组件样式，不引入完整设计系统
- 代码高亮通过 `language-*` CSS class 输出，需宿主页面引入 highlight.js 或 Prism.js
- `wwwroot/pisharp-webui.js` 提供 `PiSharpWebUi.highlightAll()` 自动检测并高亮

## 验证

测试覆盖：

- `AgentViewModel` 会转发 agent 事件并暴露最新状态
- `MessageList` 能渲染 user / assistant / tool / streaming 消息
- `AgentInterface` 能渲染模型信息、消息列表和输入区
