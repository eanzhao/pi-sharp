# AGENTS.md — pi-sharp

> AI agent guidance for working on this repository. See also [CLAUDE.md](CLAUDE.md).

## What is pi-sharp?

A C# reimplementation of [pi-mono](https://github.com/badlogic/pi-mono), the AI agent
toolkit by Mario Zechner. This is a study project: learn C# by rebuilding a real-world
AI coding agent from scratch.

## For AI Agents Working on This Repo

### Build & Test

```bash
dotnet build                          # Build all projects
dotnet test                           # Run all tests
dotnet test tests/PiSharp.Ai.Tests    # Run tests for a specific project
dotnet run --project src/PiSharp.Cli  # Run the CLI
```

### Reference Material

- `pi-mono/` contains the original TypeScript implementation (gitignored, read-only)
- `docs/` contains numbered architecture documents explaining each phase
- When implementing a project, read the corresponding pi-mono source first:
  - `PiSharp.Ai` → `pi-mono/packages/ai/src/`
  - `PiSharp.Agent` → `pi-mono/packages/agent/src/`
  - `PiSharp.Tui` → `pi-mono/packages/tui/src/`
  - `PiSharp.CodingAgent` → `pi-mono/packages/coding-agent/src/`
  - etc.

### Implementation Rules

1. **Follow the dependency order**: Ai, Tui (parallel leaves) → Agent → CodingAgent → Cli
2. **One doc + one commit per phase**: each phase adds a `docs/NN-*.md` and the implementation
3. **C# idioms over TypeScript transliteration**:
   - Use `record` for immutable data types (options, config, results)
   - Use abstract `record` + `sealed` derived types for discriminated unions (Message, Event)
   - Use `interface` (with `I` prefix) for behavioral abstractions (IProvider, IComponent)
   - Use `readonly record struct` for strongly-typed IDs (`ApiId`, `ProviderId`)
   - Use `async/await` + `IAsyncEnumerable<T>` for streaming
   - Use `CancellationToken` in all async signatures
   - Use pattern matching (`switch` expressions)
4. **Tests**: every project has a corresponding test project using xUnit
5. **Do not modify** anything under `pi-mono/` — it is the read-only reference
6. **Target**: .NET 8+ (cross-platform)

### Project Structure

Each project under `src/` follows this layout:

```
src/PiSharp.Ai/
├── PiSharp.Ai.csproj       # Project file (dependencies, target framework)
├── Messages.cs              # Core message types (Message, UserMessage, etc.)
├── Models.cs                # Model definitions and registry
├── IProvider.cs             # Provider interface
├── StreamOptions.cs         # Streaming configuration
├── Providers/               # Provider implementations
│   ├── AnthropicProvider.cs
│   ├── OpenAiProvider.cs
│   └── ...
└── ...
```

Test projects:

```
tests/PiSharp.Ai.Tests/
├── PiSharp.Ai.Tests.csproj
├── MessageTests.cs
├── ProviderTests.cs
└── ...
```

### Naming Conventions

- Namespaces: `PascalCase`, match project name (e.g., `PiSharp.Ai`, `PiSharp.Agent`)
- Types/Enums: `PascalCase` (e.g., `Message`, `StopReason`)
- Methods/Properties: `PascalCase` (e.g., `StreamAsync`, `MaxTokens`)
- Local variables/parameters: `camelCase` (e.g., `modelId`, `context`)
- Private fields: `_camelCase` (e.g., `_registry`, `_options`)
- Interfaces: `I` prefix + `PascalCase` (e.g., `IProvider`, `IComponent`)
- Async methods: `Async` suffix (e.g., `StreamAsync`, `OnStartAsync`)
- Test methods: descriptive `PascalCase` (e.g., `StreamAsync_ReturnsEvents_WhenCalled`)
