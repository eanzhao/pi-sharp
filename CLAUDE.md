# CLAUDE.md — pi-sharp

## Project Overview

pi-sharp is a C# reimplementation of [pi-mono](https://github.com/badlogic/pi-mono),
an AI agent toolkit originally written in TypeScript. The reference implementation lives
in `pi-mono/` (gitignored — read-only reference, do not modify).

## Language & Toolchain

- **Language**: C# 12+ / .NET 8+
- **Build**: `dotnet build` / `dotnet test` / `dotnet run`
- **Solution**: `PiSharp.sln`
- **Test Framework**: xUnit
- **Serialization**: System.Text.Json
- **LLM Foundation**: Microsoft.Extensions.AI (MEAI) — `IChatClient`, `ChatMessage`, `AIContent`, `AIFunction`

## Repository Layout

```
pi-sharp/
├── PiSharp.sln                    # Solution file
├── src/
│   ├── PiSharp.Ai/               # ← pi-mono/packages/ai
│   ├── PiSharp.Agent/            # ← pi-mono/packages/agent
│   ├── PiSharp.Tui/              # ← pi-mono/packages/tui
│   ├── PiSharp.CodingAgent/      # ← pi-mono/packages/coding-agent
│   ├── PiSharp.WebUi/            # ← pi-mono/packages/web-ui
│   ├── PiSharp.Mom/              # ← pi-mono/packages/mom
│   ├── PiSharp.Pods/             # ← pi-mono/packages/pods
│   └── PiSharp.Cli/              # CLI entry point
├── tests/
│   ├── PiSharp.Ai.Tests/
│   ├── PiSharp.Agent.Tests/
│   └── ...
├── docs/                          # Architecture docs (numbered, one per phase)
└── pi-mono/                       # Original TS reference (gitignored, read-only)
```

## Conventions

### C# Style

- Namespaces match project names: `PiSharp.Ai`, `PiSharp.Agent`, etc.
- Types use `PascalCase`, methods use `PascalCase`, local variables use `camelCase`
- Private fields use `_camelCase` prefix
- Use `record` for immutable data-only types (options, config, results)
- Use `class` for types with mutable state or complex behavior
- Use nested `sealed record` types for closed discriminated unions (Message, Event, StopReason)
- Use `readonly record struct` for strongly-typed ID wrappers (ApiId, ProviderId)
- Use `interface` (prefixed with `I`) for behavioral abstractions (IProvider, IComponent, IExtension)
- Use `async/await` with `Task<T>` and `IAsyncEnumerable<T>` for asynchronous operations
- Use `CancellationToken` for all async method signatures
- Prefer pattern matching (`switch` expressions) over if/else chains
- Use primary constructors for records and simple classes
- Write tests in xUnit using `[Fact]` and `[Theory]` attributes

### Workflow

- Each implementation phase = one numbered doc + one commit
- Docs live in `docs/` with `NN-topic.md` naming
- Implementation order follows dependency graph: Ai, Tui (parallel) → Agent → CodingAgent → Cli
- Reference the TypeScript source in `pi-mono/packages/` when implementing
- Each project references others via `<ProjectReference>` in `.csproj`

### Commit Messages

Use conventional commits:

```
feat(ai): add Message types and IProvider interface
docs(00): project overview and architecture plan
feat(agent): implement agent loop with tool calling
```

## MEAI (Microsoft.Extensions.AI) Strategy

The project uses MEAI as the LLM interaction foundation layer:

- **Use directly**: `IChatClient` (provider interface), `ChatMessage`/`AIContent` (message model),
  `AIFunction`/`AIFunctionFactory` (tool definitions), `ChatClientBuilder` (middleware pipeline)
- **Build on top**: Fine-grained event stream adapter (convert flat `ChatResponseUpdate` to
  `TextStart`/`TextDelta`/`TextEnd` events), Agent loop with hooks, Extension system
- **Provider SDKs**: OpenAI, Anthropic, Google official SDKs all implement `IChatClient` natively

Key NuGet packages:
- `Microsoft.Extensions.AI.Abstractions` — core interfaces and types
- `Microsoft.Extensions.AI` — middleware utilities
- `Microsoft.Extensions.AI.OpenAI` — OpenAI/Azure provider
- `Anthropic` — official Anthropic SDK with IChatClient
- `Google.GenAI` — official Google SDK with IChatClient

## Key Design Decisions

- **LLM foundation → MEAI**: `IChatClient` as provider interface, `ChatMessage`/`AIContent` as message model
- **Tool definitions → MEAI `AIFunction`**: auto-generated JSON schemas from .NET methods
- **Middleware → MEAI `ChatClientBuilder`**: logging, caching, telemetry via pipeline
- **Data interfaces → `record`**: StreamOptions, Context, ThinkingBudgets (extended beyond MEAI)
- **Behavioral interfaces → `interface`**: IComponent (`Render()`), IExtension
- **Closed union types → abstract record + sealed derived types**: AssistantMessageEvent, AgentEvent
- **Open union types → `readonly record struct` wrapper**: ApiId, ProviderId
- **Declaration merging → DI + registry + AIContent subclassing**
- **TypeScript generics → C# generics with interface constraints**
- **npm workspaces → .NET Solution with ProjectReference**
- **async/await + AsyncIterable → `async/await` + `IAsyncEnumerable<T>`**
- **JSON serialization → `System.Text.Json` with source generators**
- **Target**: .NET 8+ (cross-platform)

## Reference: pi-mono Architecture

The original pi-mono has 7 packages with this dependency graph (from package.json):

```
叶子:  ai          tui          (无内部依赖)
       ↓            ↓
中间:  agent→ai    web-ui→ai,tui   pods→agent
       ↓
上层:  coding-agent → ai, agent, tui
       ↓
       mom → ai, agent, coding-agent
```

Key abstractions to port:
- `Provider` (ai): **covered by MEAI `IChatClient`** — no custom provider interface needed
- `Tool` (ai): **covered by MEAI `AIFunction`** — auto schema generation from .NET methods
- `AssistantMessageEvent` (ai): custom event stream adapter on top of MEAI's `ChatResponseUpdate`
- `AgentLoop` (agent): orchestrates `IChatClient` calls and tool execution
- `AgentTool` (agent): wraps `AIFunction` with agent-specific hooks (beforeToolCall, afterToolCall)
- `Component` (tui): UI element with `Render()` + input handling
- `Extension` (coding-agent): plugin with lifecycle hooks
