# 05 - PiSharp.Cli

## 目标

Phase 05 实现 `PiSharp.Cli`，把前四个阶段的能力接成一个可运行的命令行入口：

1. 解析 CLI 参数并解析初始 prompt
2. 初始化 provider / model 并创建 `IChatClient`
3. 收集 `AGENTS.md` / `CLAUDE.md` 作为 project context
4. 创建 `CodingAgentSession`，以 print mode 跑完整请求

这一阶段先交付一个**非交互、可测试、可扩展**的 CLI。session 持久化、interactive TUI、extension discovery
和更完整的 provider 矩阵继续留在后续阶段。

## 参考实现

- `pi-mono/packages/coding-agent/src/cli/args.ts`
- `pi-mono/packages/coding-agent/src/cli/initial-message.ts`
- `pi-mono/packages/coding-agent/src/cli/list-models.ts`
- `pi-mono/packages/coding-agent/src/core/resource-loader.ts`
- `pi-mono/packages/coding-agent/src/main.ts`

## 本阶段范围

- `CliArgumentsParser`
- `CliContextLoader`
- `CliProviderCatalog` / `CliProviderFactory`
- `CliApplication`
- `Program.cs`
- `PiSharp.Cli.Tests`

## 设计

### 1. 参数解析保持小而稳

当前 CLI 支持的参数只覆盖 phase 05 真正需要的主链路：

- `--provider`
- `--model`
- `--api-key`
- `--cwd`
- `--system-prompt`
- `--append-system-prompt`
- `--tools` / `--no-tools`
- `--no-context-files`
- `--thinking`
- `--list-models`
- `--verbose`

这与 pi-mono 的完整参数面相比小很多，但已经足够支撑：

- 一次性 print-mode 调用
- 仓库上下文注入
- provider/model 显式选择
- 基础模型列表查看

### 2. 上下文文件加载对齐 pi-mono 的“祖先目录扫描”

`CliContextLoader` 参考 `resource-loader.ts`：

- 从工作目录一路向上扫描祖先目录
- 每个目录按 `AGENTS.md` → `CLAUDE.md` 的优先级挑一个 context 文件
- 按从外到内的顺序注入 system prompt

这样仓库根规则、子目录局部规则都能自然叠加到 `CodingAgentSession`。

### 3. Provider 初始化放在 CLI

phase 01 已明确：`PiSharp.Ai` 只负责 registry，不负责真实 provider 初始化。
因此 phase 05 新增 `CliProviderFactory`：

- 暴露 `ProviderConfiguration`
- 提供已知 `ModelMetadata`
- 负责把 `apiKey + modelId` 转成 `IChatClient`

默认实现先接 OpenAI，并保留 catalog 结构，后面继续加 Anthropic / Google 时不需要改 CLI 主流程。

### 4. 运行模式先收敛为 print mode

`CliApplication` 当前只做一件事：

1. 收集 stdin、`@file` 和 positional message
2. 构造 `CodingAgentSessionOptions`
3. 订阅 `AgentEvent`
4. 把 assistant 文本流直接写到 stdout
5. 把 tool 执行诊断写到 stderr（`--verbose` 时）

这意味着 phase 05 已经把：

- `PiSharp.Ai` 的流式内容
- `PiSharp.Agent` 的事件系统
- `PiSharp.CodingAgent` 的工具和 prompt 装配

都串进了一个真实 CLI 闭环。

### 5. 为什么暂时不接交互 TUI

虽然 `PiSharp.Tui` 已经具备基础组件和差分渲染，但当前终端输入层仍停留在较基础的阶段。
如果 phase 05 直接把 interactive mode、raw input、session picker 一起加入，范围会迅速膨胀到接近
pi-mono 主程序。

因此这一阶段只完成：

- 非交互命令入口
- 面向后续 interactive mode 的 provider/context/session 组装层

后面一旦终端输入能力补齐，就可以在 `CliApplication` 之上追加真正的 TUI 模式，而不需要推翻现有接线。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.Cli |
|---|---|
| `parseArgs()` | `CliArgumentsParser.Parse()` |
| `buildInitialMessage()` | `CliApplication.BuildInitialPromptAsync()` |
| `loadProjectContextFiles()` | `CliContextLoader.Load()` |
| `listModels()` | `CliApplication.ListModelsAsync()` |
| `main.ts` print path | `CliApplication.RunAsync()` |

## 当前取舍

- 只实现 print mode，不做 interactive / rpc
- 不做 session 持久化、resume、fork
- provider 目录先接 OpenAI，其它 provider 后续补
- `@file` 只按文本文件处理，不做图片
- 模型列表先展示 CLI 侧已知模型，不做远程模型发现

## 验证

测试覆盖：

- 参数解析主路径
- 祖先目录 context 文件加载顺序
- CLI print mode 调用时把 context 注入到 system prompt
- `--list-models` 输出已知模型目录
