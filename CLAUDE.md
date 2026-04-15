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

## Key Design Decisions

- **Data interfaces → `record`**: StreamOptions, Tool (schema), Context, ThinkingBudgets
- **Behavioral interfaces → `interface`**: IProvider (`StreamAsync()`), IComponent (`Render()`), IExtension
- **Closed union types → abstract record + sealed derived types**: Message, AssistantMessageEvent, StopReason
- **Open union types → `readonly record struct` wrapper**: ApiId, ProviderId
- **Declaration merging → DI + registry pattern**: custom messages via `Dictionary<string, Func<JsonElement, AgentMessage>>`
- **TypeScript generics → C# generics with interface constraints** (`where T : IProvider`)
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
- `Provider` (ai): LLM provider with `StreamAsync()` / `CompleteAsync()`
- `AgentLoop` (agent): orchestrates LLM calls and tool execution
- `AgentTool` (agent): tool definition with schema + execute
- `Component` (tui): UI element with `Render()` + input handling
- `Extension` (coding-agent): plugin with lifecycle hooks
