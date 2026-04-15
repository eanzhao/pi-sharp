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
| 01 | `PiSharp.Ai` | 消息类型、模型定义、IProvider 接口、流式 API |
| 02 | `PiSharp.Tui` | 终端抽象、IComponent 接口、差分渲染（与 Ai 无依赖，可并行） |
| 03 | `PiSharp.Agent` | Agent 循环、工具类型、事件系统（依赖 Ai） |
| 04 | `PiSharp.CodingAgent` | 内置工具、会话管理、扩展 API（依赖 Ai + Agent + Tui） |
| 05 | `PiSharp.Cli` | CLI 入口，串联所有模块 |
| 06 | `PiSharp.WebUi` | Web UI 组件（依赖 Ai + Tui，Blazor / ASP.NET） |
| 07 | `PiSharp.Pods` | GPU Pod 管理（依赖 Agent） |
| 08 | `PiSharp.Mom` | Slack 集成（依赖 Ai + Agent + CodingAgent） |

### 关键设计决策

| 维度 | TypeScript (pi-mono) | C# (pi-sharp) |
|------|----------------------|----------------|
| 数据型接口 | `interface` (纯数据) | `record` (不可变) 或 `class` (可变) |
| 行为型接口 | `interface` (方法签名) | `interface` (如 `IProvider`, `IComponent`) |
| 封闭联合类型 | 联合类型 `A \| B \| C` | 抽象 record + 派生类型 (判别联合模式) |
| 开放联合类型 | `string & {}` / 声明合并 | 强类型 ID 包装 (如 `record ApiId(string Value)`) |
| 泛型 | 泛型参数 + 条件类型 | 泛型参数 + 接口/类约束 (`where T : IProvider`) |
| 错误处理 | try/catch + Result | 异常 + `Result<T>` 模式 (可选) |
| 异步 | async/await + AsyncIterable | `async/await` + `Task<T>` + `IAsyncEnumerable<T>` |
| 序列化 | JSON 原生 | `System.Text.Json` (源生成器优化) |
| 扩展机制 | 动态导入 + 声明合并 | 依赖注入 + 插件接口 + `Assembly.Load` |
| 编译目标 | Node.js / 浏览器 | .NET 8+ (跨平台) |
| 包管理 | npm workspaces | .NET Solution + 多项目引用 |
| 包配置文件 | package.json | `.csproj` |
| 测试框架 | Vitest | xUnit / NUnit |
| 代码质量 | Biome | dotnet format + Roslyn 分析器 |

### TypeScript → C# 映射详解

**数据型 vs 行为型接口**

pi-mono 中大部分 `interface` 是纯数据形状（如 `StreamOptions`, `Tool`, `Context`,
`ThinkingBudgets`），应映射为 C# `record`（不可变）或 `class`（需要可变性时）。
只有需要多态行为的接口（如 `ApiProvider` 的 `stream()` 方法、TUI 的 `Component.render()`）
才映射为 C# `interface`。

```csharp
// 数据型 → record
public record StreamOptions(
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null
);

// 行为型 → interface
public interface IProvider
{
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, Context context, StreamOptions options,
        CancellationToken ct = default);
}
```

**封闭 vs 开放联合**

封闭联合（如 `Message`, `AssistantMessageEvent`, `StopReason`）用抽象 record + 派生类型：

```csharp
public abstract record Message
{
    public sealed record User(UserMessage Content) : Message;
    public sealed record Assistant(AssistantMessage Content) : Message;
    public sealed record ToolResult(ToolResultMessage Content) : Message;
}

// 配合 switch 表达式进行模式匹配
var text = message switch
{
    Message.User u => u.Content.Text,
    Message.Assistant a => a.Content.Text,
    Message.ToolResult t => t.Content.Output,
    _ => throw new InvalidOperationException()
};
```

开放联合（如 `Api` 和 `Provider` 类型允许任意字符串扩展）用强类型 ID：

```csharp
public readonly record struct ApiId(string Value)
{
    public static readonly ApiId OpenAI = new("openai");
    public static readonly ApiId Anthropic = new("anthropic");
    // 允许任意扩展
    public static implicit operator ApiId(string value) => new(value);
}
```

**异步与事件流**

pi-mono 的核心流式 API 基于 `AsyncIterable<AssistantMessageEvent>`。
C# 有原生的 `IAsyncEnumerable<T>` 支持，天然适配：

```csharp
public interface IProvider
{
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        Context context,
        StreamOptions? options = null,
        CancellationToken ct = default);
}

// 消费端
await foreach (var evt in provider.StreamAsync(model, ctx))
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

### 扩展机制设计

pi-mono 的扩展系统是其核心特性之一，涉及三个层面：

1. **声明合并**（`CustomAgentMessages`）— TypeScript 特有，允许第三方扩展消息类型。
   C# 没有声明合并，采用 **注册表模式 + 依赖注入**：通过
   `IServiceCollection` 注册自定义消息反序列化器，或维护
   `Dictionary<string, Func<JsonElement, AgentMessage>>` 反序列化表。

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
