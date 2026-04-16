# pi-sharp

C# reimplementation of [pi-mono](https://github.com/badlogic/pi-mono) — an AI agent toolkit.

## What is this?

A learning project that rebuilds pi-mono's architecture in C#, project by project:

| Project | pi-mono | Status | Description |
|---------|---------|--------|-------------|
| `PiSharp.Ai` | `@mariozechner/pi-ai` | Implemented | MEAI-based stream adapter, usage tracking, provider/model registries |
| `PiSharp.Tui` | `@mariozechner/pi-tui` | Implemented | Terminal UI with differential rendering, component system, key/shortcut handling |
| `PiSharp.Agent` | `@mariozechner/pi-agent-core` | Implemented | Agent loop, tool calling (sequential/parallel), steering/follow-up, state management |
| `PiSharp.CodingAgent` | `@mariozechner/pi-coding-agent` | Implemented | Built-in tools (read/write/edit/bash/grep/find/ls), session persistence, compaction, settings, extensions |
| `PiSharp.Cli` | coding-agent CLI | Implemented | Interactive TUI + print + JSON modes, session resume/fork, multi-provider, MEAI middleware |
| `PiSharp.WebUi` | `@mariozechner/pi-web-ui` | Implemented | Blazor chat components, markdown rendering, syntax highlighting, SSR-friendly |
| `PiSharp.Pods` | `@mariozechner/pi` | Implemented | GPU pod management, SSH orchestration, vLLM deployment, interactive agent |
| `PiSharp.Mom` | `@mariozechner/pi-mom` | Implemented | Slack Socket Mode bot runtime backed by `CodingAgentSession` |

## Why?

- **Learn C#** through a real-world, non-trivial project
- **Understand pi-mono's architecture** by reimplementing it from scratch
- **Explore C#'s strengths** in systems with complex type hierarchies (records, interfaces, pattern matching, async streams)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

## Quick Start

```bash
# Build and test
dotnet build
dotnet test

# Run the coding agent CLI
dotnet run --project src/PiSharp.Cli -- "Summarize the repository"

# Interactive mode (when stdin is a TTY)
dotnet run --project src/PiSharp.Cli

# JSON output for scripting
dotnet run --project src/PiSharp.Cli -- --json "What does this project do?"

# Resume or fork a previous session
dotnet run --project src/PiSharp.Cli -- --resume latest
dotnet run --project src/PiSharp.Cli -- --fork latest "Try a different approach"

# GPU pod management
dotnet run --project src/PiSharp.Cli -- pods setup dc1 "ssh root@1.2.3.4"
dotnet run --project src/PiSharp.Cli -- pods start Qwen/Qwen2.5-Coder-32B-Instruct --name qwen
dotnet run --project src/PiSharp.Cli -- pods agent qwen -i
```

## Project Structure

```
pi-sharp/
├── PiSharp.slnx              # Solution file
├── src/
│   ├── PiSharp.Ai/           # LLM provider abstraction (MEAI adapter)
│   ├── PiSharp.Tui/          # Terminal UI library
│   ├── PiSharp.Agent/        # Agent loop engine
│   ├── PiSharp.CodingAgent/  # Coding agent core + tools + session + settings
│   ├── PiSharp.Cli/          # CLI entry point (interactive / print / JSON)
│   ├── PiSharp.WebUi/        # Blazor chat component library
│   ├── PiSharp.Pods/         # GPU pod management library
│   ├── PiSharp.Pods.Cli/     # Pod management executable
│   └── PiSharp.Mom/          # Slack bot runtime
├── tests/
│   ├── PiSharp.Ai.Tests/
│   ├── PiSharp.Tui.Tests/
│   ├── PiSharp.Agent.Tests/
│   ├── PiSharp.CodingAgent.Tests/
│   ├── PiSharp.Cli.Tests/
│   ├── PiSharp.WebUi.Tests/
│   ├── PiSharp.Pods.Tests/
│   └── PiSharp.Mom.Tests/
└── docs/                      # Architecture docs (one per phase)
```

## Docs

Each implementation phase has a corresponding document in `docs/`:

- [00 - Project Overview](docs/00-project-overview.md) — Architecture analysis, MEAI decision, implementation plan
- [01 - PiSharp.Ai](docs/01-ai-layer.md) — Stream adapter, usage tracking, provider/model registries
- [02 - PiSharp.Tui](docs/02-tui.md) — Terminal UI, differential rendering, component model
- [03 - PiSharp.Agent](docs/03-agent.md) — Agent loop, tool execution, steering/follow-up, state
- [04 - PiSharp.CodingAgent](docs/04-coding-agent.md) — Tools, session persistence, compaction, settings, extensions
- [05 - PiSharp.Cli](docs/05-cli.md) — CLI modes, provider bootstrapping, session lifecycle
- [06 - PiSharp.WebUi](docs/06-web-ui.md) — Blazor chat components, markdown/code rendering
- [07 - PiSharp.Pods](docs/07-pods.md) — Pod config, SSH orchestration, vLLM deployment
- [08 - PiSharp.Mom](docs/08-mom.md) — Slack Socket Mode runtime, per-channel sessions, workspace persistence

## Reference

- [pi-mono](https://github.com/badlogic/pi-mono) — Original TypeScript implementation
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) — LLM foundation layer
- [C# Documentation](https://learn.microsoft.com/en-us/dotnet/csharp/) — Language reference
