# 00 - 项目总览：pi-sharp

## 目标

用 C# 重新实现 [pi-mono](https://github.com/badlogic/pi-mono)（一个 AI 编程智能体工具包），
达到两个目的：

1. **学习 C#** — 通过实战掌握 C# 的类型系统、接口、泛型、异步编程、依赖注入等核心特性。
2. **理解 pi-mono 架构** — 逐包拆解并重建，深入理解 AI Agent 从底层 LLM API 到上层交互界面的全栈设计。

## pi-mono 是什么

pi-mono 是 Mario Zechner（libGDX 作者）开源的 AI 智能体工具包，TypeScript 实现，
采用「反框架」理念——提供可组合的原语而非全家桶。核心特点：

- 统一的多 Provider LLM API（Anthropic / OpenAI / Google / Mistral / Azure / Bedrock 等 13+）
- 最小化的 Agent 循环引擎（5 个源文件）
- 强大的扩展 API（自定义工具、UI 组件、子智能体）
- 内置编码工具（read / write / edit / bash / grep / find / ls）
- 终端 UI 库（差分渲染）和 Web UI 组件库

## 原始架构：7 个包

| 包名 | npm 名 | 职责 |
|------|--------|------|
| `ai` | `@mariozechner/pi-ai` | 统一多 Provider LLM API，流式响应，模型发现 |
| `agent` | `@mariozechner/pi-agent-core` | Agent 循环引擎，工具调用，状态管理 |
| `coding-agent` | `@mariozechner/pi-coding-agent` | 编码智能体 CLI，内置工具，会话管理，扩展系统 |
| `tui` | `@mariozechner/pi-tui` | 终端 UI 库，差分渲染，组件系统 |
| `web-ui` | `@mariozechner/pi-web-ui` | Web 聊天组件库 |
| `mom` | `@mariozechner/pi-mom` | Slack 机器人，消息委派给编码智能体 |
| `pods` | `@mariozechner/pi` | GPU Pod 管理 CLI，vLLM 部署 |

### 依赖关系（根据 package.json 实际声明）

```
叶子包（无内部依赖）:
  ai
  tui

中间层:
  agent        → ai
  pods         → agent
  web-ui       → ai, tui

上层:
  coding-agent → ai, agent, tui
  mom          → ai, agent, coding-agent
```

完整依赖图（箭头表示"被依赖"方向）：

```
ai ──→ agent ──→ coding-agent ──→ mom
│        │            ↑
│        └──→ pods    │
│                     │
└──→ web-ui           │
       ↑              │
tui ───┴──────────────┘
```

读法：`ai → agent` 表示 agent 依赖 ai。
- ai, tui：无内部依赖（叶子）
- agent：依赖 ai
- pods：依赖 agent
- web-ui：依赖 ai, tui
- coding-agent：依赖 ai, agent, tui
- mom：依赖 ai, agent, coding-agent

## 基础设施决策：采用 Microsoft.Extensions.AI (MEAI)

经过调研，决定从一开始就采用 [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
作为 LLM 交互的基础层。MEAI 于 2025-05-21 GA，当前版本 10.4.x。

### 为什么用 MEAI

MEAI 与 pi-mono 的 `ai` 包高度重叠，可以省去大量底层工作：

| pi-mono 概念 | MEAI 对应 | 匹配度 |
|---|---|---|
| `Message` (User/Assistant/ToolResult) | `ChatMessage` + `ChatRole` + `AIContent` 层级 | 直接映射 |
| `TextContent` / `ImageContent` / `ThinkingContent` | `TextContent` / `ImageContent` / `TextReasoningContent` | 直接映射 |
| `ToolCall` | `FunctionCallContent` | 直接映射 |
| `Provider.stream()` | `IChatClient.GetStreamingResponseAsync()` | 概念匹配 |
| `StreamOptions` (temperature, maxTokens...) | `ChatOptions` | 直接映射 |
| `Tool` + JSON Schema | `AIFunction` + `AIFunctionFactory`（自动生成 schema） | 直接映射 |
| 多 Provider 支持 | OpenAI / Anthropic / Google 官方 SDK 均已实现 `IChatClient` | 零 HTTP 代码 |

### MEAI 不覆盖、需自建的部分

| pi-mono 概念 | 差距说明 |
|---|---|
| 细粒度事件流 (`text_start`/`text_delta`/`text_end`...) | MEAI 只给扁平的 `IAsyncEnumerable<ChatResponseUpdate>`，需写适配层 |
| Agent 循环（steering messages, beforeToolCall/afterToolCall 钩子） | `FunctionInvokingChatClient` 太简单，需自己写 agent loop |
| Usage 中的 cache read/write 和费用追踪 | MEAI 的 `UsageDetails` 没有这些字段，需扩展 |
| 每条消息上的 provider/model/timestamp 元数据 | MEAI 把这些放在 `ChatResponse` 而非 `ChatMessage` 上 |

### 分层架构

```
┌─────────────────────────────────────┐
│  PiSharp.CodingAgent / Cli / Mom    │  ← 自己写
├─────────────────────────────────────┤
│  PiSharp.Agent (Agent Loop + Events)│  ← 自己写，调用 IChatClient
├─────────────────────────────────────┤
│  PiSharp.Ai (事件适配 + 扩展类型)     │  ← 薄适配层，基于 MEAI
├─────────────────────────────────────┤
│  MEAI Abstractions + Middleware     │  ← 直接用
│  (IChatClient, ChatMessage, AITool) │
├─────────────────────────────────────┤
│  Provider SDKs                      │  ← 直接用，已实现 IChatClient
│  (Anthropic / OpenAI / Google)      │
└─────────────────────────────────────┘
```

- **直接使用 MEAI**：`IChatClient` 作为 provider 接口、`ChatMessage`/`AIContent` 作为消息模型、
  `AIFunction` 做工具定义、middleware pipeline 做日志/缓存/遥测
- **自己在上面构建**：细粒度事件流适配器（将 `ChatResponseUpdate` 转换为 `text_start`/`text_delta`/`text_end` 等事件）、
  Agent 循环（钩子、steering messages、上下文变换）、扩展系统

### 核心 NuGet 依赖

| 包 | 用途 |
|---|---|
| `Microsoft.Extensions.AI.Abstractions` | 核心接口和类型 (`IChatClient`, `ChatMessage`, `AIContent`) |
| `Microsoft.Extensions.AI` | Middleware 工具 (`ChatClientBuilder`, `FunctionInvokingChatClient`, 日志/缓存) |
| `Microsoft.Extensions.AI.OpenAI` | OpenAI / Azure OpenAI provider |
| `Anthropic` | Anthropic 官方 SDK（内置 `IChatClient` 支持） |
| `Google.GenAI` | Google 官方 SDK（内置 `IChatClient` 支持） |

## C# 重新实现方案

### 项目映射

```
pi-sharp/
├── PiSharp.sln                    # 解决方案文件
├── src/
│   ├── PiSharp.Ai/               ← pi-ai: LLM Provider 抽象层
│   ├── PiSharp.Agent/            ← pi-agent-core: Agent 循环引擎
│   ├── PiSharp.CodingAgent/      ← pi-coding-agent: 编码智能体核心
│   ├── PiSharp.Tui/              ← pi-tui: 终端 UI 库
│   ├── PiSharp.WebUi/            ← pi-web-ui: Web UI 组件
│   ├── PiSharp.Mom/              ← pi-mom: Slack 集成
│   └── PiSharp.Pods/             ← pi (@mariozechner/pi): GPU Pod 管理
├── src/PiSharp.Cli/              ← CLI 入口
├── tests/
│   ├── PiSharp.Ai.Tests/
│   ├── PiSharp.Agent.Tests/
│   └── ...                       # 每个包对应一个测试项目
└── pi-mono/                       # 原始 TS 参考（gitignored, 只读）
```

### 实现顺序

按照依赖关系从底向上（`ai` 和 `tui` 是两个独立的叶子包，可并行）：

| 阶段 | 项目 | 要点 |
|------|------|------|
| 01 | `PiSharp.Ai` | 基于 MEAI 的事件流适配层、扩展 Usage 类型、Provider 注册表 |
| 02 | `PiSharp.Tui` | 终端抽象、IComponent 接口、差分渲染（与 Ai 无依赖，可并行） |
| 03 | `PiSharp.Agent` | Agent 循环、基于 `AIFunction` 的工具系统、事件系统（依赖 Ai） |
| 04 | `PiSharp.CodingAgent` | 内置工具、会话管理、扩展 API（依赖 Ai + Agent + Tui） |
| 05 | `PiSharp.Cli` | CLI 入口，串联所有模块 |
| 06 | `PiSharp.WebUi` | Web UI 组件（依赖 Ai + Tui，Blazor / ASP.NET） |
| 07 | `PiSharp.Pods` | GPU Pod 管理（依赖 Agent） |
| 08 | `PiSharp.Mom` | Slack 集成（依赖 Ai + Agent + CodingAgent） |

### 关键设计决策

| 维度 | TypeScript (pi-mono) | C# (pi-sharp) |
|------|----------------------|----------------|
| LLM 基础层 | 自建多 Provider 抽象 | **MEAI** (`IChatClient`, `ChatMessage`, `AIContent`) |
| Provider 接口 | `ApiProvider.stream()` | `IChatClient.GetStreamingResponseAsync()` (MEAI) |
| 消息类型 | 自定义 `Message` union | MEAI `ChatMessage` + `ChatRole` + `AIContent` 层级 |
| 工具定义 | `Tool<TSchema>` + TypeBox | MEAI `AIFunction` + `AIFunctionFactory` (自动 schema 生成) |
| 数据型接口 | `interface` (纯数据) | `record` (不可变) 或 `class` (可变) |
| 行为型接口 | `interface` (方法签名) | `interface` (如 `IComponent`, `IExtension`) |
| 封闭联合类型 | 联合类型 `A \| B \| C` | 抽象 record + 派生类型 (判别联合模式) |
| 开放联合类型 | `string & {}` / 声明合并 | 强类型 ID 包装 (如 `record ApiId(string Value)`) |
| 泛型 | 泛型参数 + 条件类型 | 泛型参数 + 接口/类约束 |
| 错误处理 | try/catch + Result | 异常 + `Result<T>` 模式 (可选) |
| 异步 | async/await + AsyncIterable | `async/await` + `Task<T>` + `IAsyncEnumerable<T>` |
| 序列化 | JSON 原生 | `System.Text.Json` (源生成器优化) |
| 扩展机制 | 动态导入 + 声明合并 | 依赖注入 + 插件接口 + `Assembly.Load` |
| 中间件 | 无（自建） | MEAI `ChatClientBuilder` pipeline (日志/缓存/遥测) |
| 编译目标 | Node.js / 浏览器 | .NET 8+ (跨平台) |
| 包管理 | npm workspaces | .NET Solution + 多项目引用 |
| 测试框架 | Vitest | xUnit |
| 代码质量 | Biome | dotnet format + Roslyn 分析器 |

### TypeScript → C# 映射详解（基于 MEAI）

**Provider 接口：直接使用 MEAI 的 IChatClient**

pi-mono 自建了 `ApiProvider` 接口和 13+ 个 HTTP 级 provider 实现。
在 pi-sharp 中，直接使用 MEAI 的 `IChatClient`，各 provider SDK 已实现：

```csharp
// 不需要自建 IProvider —— 直接用 MEAI 的 IChatClient
using Anthropic;
using Microsoft.Extensions.AI;

// Anthropic
IChatClient anthropic = new AnthropicClient("sk-...").AsIChatClient("claude-opus-4-6");

// OpenAI
IChatClient openai = new OpenAIClient("sk-...").AsChatClient("gpt-4o");

// 加上 middleware pipeline
IChatClient client = new ChatClientBuilder(anthropic)
    .UseOpenTelemetry()
    .UseLogging()
    .Build();
```

**消息模型：使用 MEAI 的 ChatMessage + AIContent**

pi-mono 的 `Message` 联合类型直接映射到 MEAI 的 `ChatMessage`：

```csharp
// pi-mono: new UserMessage([{ type: "text", text: "Hello" }])
// pi-sharp (MEAI):
var userMsg = new ChatMessage(ChatRole.User, "Hello");

// 带图片的消息
var multiModal = new ChatMessage(ChatRole.User, [
    new TextContent("What's in this image?"),
    new ImageContent(imageBytes, "image/png")
]);

// 工具结果
var toolResult = new ChatMessage(ChatRole.Tool, [
    new FunctionResultContent("call_123", "get_weather", result: "Sunny, 25°C")
]);
```

**工具定义：使用 MEAI 的 AIFunction**

pi-mono 用 TypeBox 手写 JSON Schema，MEAI 从 .NET 方法自动生成：

```csharp
// pi-mono: 手写 { type: "object", properties: { city: { type: "string" } } }
// pi-sharp (MEAI): 自动从方法签名生成 schema
AIFunction weatherTool = AIFunctionFactory.Create(
    (string city) => $"Weather in {city}: sunny, 25°C",
    "get_weather",
    "Gets the current weather for a city");
```

**流式 API：MEAI 基础 + 自建事件适配层**

MEAI 提供扁平的 `IAsyncEnumerable<ChatResponseUpdate>`，
PiSharp.Ai 在此之上构建细粒度事件流：

```csharp
// MEAI 底层：扁平 update 流
await foreach (var update in client.GetStreamingResponseAsync(messages, options))
{
    Console.Write(update.Text);
}

// PiSharp.Ai 适配层：细粒度事件（自建）
public abstract record AssistantMessageEvent
{
    public sealed record TextStart : AssistantMessageEvent;
    public sealed record TextDelta(string Text) : AssistantMessageEvent;
    public sealed record TextEnd : AssistantMessageEvent;
    public sealed record ThinkingStart : AssistantMessageEvent;
    public sealed record ThinkingDelta(string Text) : AssistantMessageEvent;
    public sealed record ThinkingEnd : AssistantMessageEvent;
    public sealed record ToolCallStart(string CallId, string Name) : AssistantMessageEvent;
    public sealed record ToolCallDelta(string CallId, string ArgumentsDelta) : AssistantMessageEvent;
    public sealed record ToolCallEnd(string CallId) : AssistantMessageEvent;
    public sealed record Done(ChatFinishReason? FinishReason, UsageDetails? Usage) : AssistantMessageEvent;
    public sealed record Error(Exception Exception) : AssistantMessageEvent;
}

// 适配器：将 MEAI 的扁平流转换为细粒度事件流
await foreach (var evt in StreamAdapter.ToEvents(client, messages, options))
{
    switch (evt)
    {
        case AssistantMessageEvent.TextDelta delta:
            Console.Write(delta.Text);
            break;
        // ...
    }
}
```

**封闭联合（非 MEAI 部分）**

TUI 组件、Agent 事件等 pi-sharp 自建类型仍使用抽象 record + sealed 派生类型：

```csharp
public abstract record AgentEvent
{
    public sealed record AgentStart : AgentEvent;
    public sealed record TurnStart(int Turn) : AgentEvent;
    public sealed record ToolExecutionStart(string ToolName, string CallId) : AgentEvent;
    public sealed record ToolExecutionEnd(string ToolName, string CallId) : AgentEvent;
    public sealed record TurnEnd(int Turn) : AgentEvent;
    public sealed record AgentEnd : AgentEvent;
}
```

**开放联合**

Api/Provider 标识符等开放类型用强类型 ID 包装：

```csharp
public readonly record struct ApiId(string Value)
{
    public static readonly ApiId OpenAI = new("openai");
    public static readonly ApiId Anthropic = new("anthropic");
    public static implicit operator ApiId(string value) => new(value);
}
```

### 扩展机制设计

pi-mono 的扩展系统是其核心特性之一，涉及三个层面：

1. **声明合并**（`CustomAgentMessages`）— TypeScript 特有，允许第三方扩展消息类型。
   C# 没有声明合并，但 MEAI 的 `AIContent` 支持 `[JsonPolymorphic]` 多态序列化，
   可通过子类化 `AIContent` 自定义内容类型。同时采用 **注册表模式 + 依赖注入** 注册自定义消息。

2. **动态加载**（`extensions/loader.ts`）— TypeScript 通过 `import()` 动态加载扩展模块。
   C# 可通过 `Assembly.LoadFrom()` 加载 DLL 插件，或使用 `MEF`
   (Managed Extensibility Framework) / `System.Composition`。

3. **生命周期钩子**（`Extension` 接口）— 直接映射为 `IExtension` 接口：

```csharp
public interface IExtension
{
    string Name { get; }
    Task OnStartAsync(ExtensionContext context, CancellationToken ct = default);
    Task<BeforeToolCallResult?> OnBeforeToolCallAsync(
        BeforeToolCallContext context, CancellationToken ct = default);
    Task<AfterToolCallResult?> OnAfterToolCallAsync(
        AfterToolCallContext context, CancellationToken ct = default);
}
```

> 注意：扩展机制是最复杂的映射点，具体方案将在 04 号文档中详细展开。

## 文档计划

每个实现阶段对应一篇文档：

| 编号 | 主题 |
|------|------|
| 00 | 项目总览（本文） |
| 01 | LLM API 层：消息、模型、Provider |
| 02 | 终端 UI：组件、渲染、键盘（与 01 无依赖，可并行） |
| 03 | Agent 引擎：循环、工具、事件（依赖 01） |
| 04 | 编码智能体：工具、会话、扩展（依赖 01 + 02 + 03） |
| 05 | CLI 入口与集成 |
| 06+ | Web UI / Pods / Slack |

每篇文档包含：
- 对应 pi-mono 包的架构分析
- C# 实现的设计决策
- 关键类型和接口定义
- 与 TypeScript 版的对照
