# pi-sharp

C# reimplementation of [pi-mono](https://github.com/badlogic/pi-mono) — an AI agent toolkit.

## What is this?

A learning project that rebuilds pi-mono's architecture in C#, project by project:

| Project | Status | Description |
|---------|--------|-------------|
| `PiSharp.Ai` | Implemented | Unified multi-provider LLM API |
| `PiSharp.Agent` | Implemented | Agent loop, tool calling, state management |
| `PiSharp.Tui` | Implemented | Terminal UI with differential rendering |
| `PiSharp.CodingAgent` | Planned | Coding agent with built-in tools and extensions |
| `PiSharp.WebUi` | Planned | Web chat UI components |
| `PiSharp.Mom` | Planned | Slack bot integration |
| `PiSharp.Pods` | Planned | GPU pod management for vLLM |

## Why?

- **Learn C#** through a real-world, non-trivial project
- **Understand pi-mono's architecture** by reimplementing it from scratch
- **Explore C#'s strengths** in systems with complex type hierarchies (records, interfaces, pattern matching, async streams)

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later

## Build

```bash
dotnet build    # Build all projects
dotnet test     # Run all tests
dotnet run --project src/PiSharp.Cli  # Run the CLI
```

## Project Structure

```
pi-sharp/
├── PiSharp.sln               # Solution file
├── src/
│   ├── PiSharp.Ai/           # LLM provider abstraction
│   ├── PiSharp.Agent/        # Agent loop engine
│   ├── PiSharp.Tui/          # Terminal UI library
│   ├── PiSharp.CodingAgent/  # Coding agent core
│   ├── PiSharp.Cli/          # CLI entry point
│   └── ...
├── tests/
│   ├── PiSharp.Ai.Tests/
│   └── ...
└── docs/                     # Architecture docs (one per implementation phase)
    └── 00-project-overview.md
```

## Docs

Each implementation phase has a corresponding document in `docs/`:

- [00 - Project Overview](docs/00-project-overview.md) — Architecture analysis and implementation plan
- [01 - PiSharp.Ai](docs/01-ai-layer.md) — MEAI-based stream adapter, usage, registries
- [02 - PiSharp.Tui](docs/02-tui.md) — terminal UI foundation and differential rendering
- [03 - PiSharp.Agent](docs/03-agent.md) — agent loop, tool execution, stateful wrapper

## Reference

- [pi-mono](https://github.com/badlogic/pi-mono) — Original TypeScript implementation
- [C# Documentation](https://learn.microsoft.com/en-us/dotnet/csharp/) — Language reference
- [.NET API Reference](https://learn.microsoft.com/en-us/dotnet/api/) — Framework API docs
