# 04 - PiSharp.CodingAgent

## 目标

Phase 04 实现 `PiSharp.CodingAgent` 的最小核心层，承接 `PiSharp.Agent`，为后续 CLI 提供一个可复用的“编码会话运行时”：

1. 默认 system prompt 构建
2. 内置 coding tools
3. 有状态 session wrapper
4. 最小 extension hook

本阶段实现编码智能体的核心运行时和基础设施层：内置工具、会话持久化、上下文压缩、设置管理和扩展加载。

## 参考实现

- `pi-mono/packages/coding-agent/src/core/agent-session.ts`
- `pi-mono/packages/coding-agent/src/core/system-prompt.ts`
- `pi-mono/packages/coding-agent/src/core/tools/index.ts`
- `pi-mono/packages/coding-agent/src/core/tools/read.ts`
- `pi-mono/packages/coding-agent/src/core/tools/write.ts`
- `pi-mono/packages/coding-agent/src/core/tools/edit.ts`
- `pi-mono/packages/coding-agent/src/core/tools/grep.ts`
- `pi-mono/packages/coding-agent/src/core/tools/find.ts`
- `pi-mono/packages/coding-agent/src/core/tools/ls.ts`
- `pi-mono/packages/coding-agent/src/core/tools/bash.ts`
- `pi-mono/packages/coding-agent/src/core/extensions/types.ts`

## 本阶段范围

- `CodingAgentSystemPrompt.Build()`
- `CodingAgentTools` built-in tool registry
- 默认 tools：`read` / `bash` / `edit` / `write`
- 可选 tools：`grep` / `find` / `ls`
- `CodingAgentSession` / `CodingAgentSessionOptions`
- `ICodingAgentExtension` + `CodingAgentSessionBuilder`
- `SessionManager` — JSONL 持久化、树状历史、分支、上下文构建
- `CompactionService` + `TokenEstimation` — token 估算、裁剪点检测、LLM 摘要生成
- `SettingsManager` + `CodingAgentSettings` — 全局 + 项目两级 JSON 设置
- `ExtensionLoader` — 基于 Assembly 的扩展动态加载
- xUnit 测试覆盖全部模块

## 设计

### 1. Session 是 CodingAgent 的主入口

参考 pi-mono 的 `AgentSession`，C# 版本提供 `CodingAgentSession` 作为高层状态封装：

- 内部持有一个 `PiSharp.Agent.Agent`
- 暴露 `PromptAsync()` / `ContinueAsync()` / `Steer()` / `FollowUp()`
- 对外保留 `AgentState`
- 负责把 prompt、tool 集合、extension 配置收敛成一个完整 agent runtime

内存态 orchestration 由 `CodingAgentSession` 负责；持久化、分支和上下文重建由 `SessionManager` 负责。

### 2. System prompt builder

沿用 pi-mono 的思路，system prompt 由以下几部分拼装：

- 默认角色说明
- 当前启用 tool 的一行摘要
- guideline 列表
- project context files
- 当前日期和 working directory

同时支持：

- `CustomPrompt`：完全替换默认模板
- `AppendSystemPrompt`：在默认或自定义 prompt 后追加额外约束

这样 phase 05 的 CLI 就可以把 `AGENTS.md`、仓库规则和用户配置拼进同一个入口。

### 3. 内置工具

这一阶段实现 7 个基础工具：

- `read`：按行读取文本文件，支持 `offset` / `limit`
- `write`：创建或覆写文件
- `edit`：精确文本替换
- `bash`：在 working directory 中执行 shell 命令
- `grep`：正则搜索文件内容
- `find`：按相对路径名查找文件/目录
- `ls`：列出目录内容

实现策略保持“最小但可用”：

- 统一限制在 working directory 内部
- `find` / `grep` / `ls` 默认跳过 `.git`、`bin`、`obj`
- 不实现 pi-mono 里的 UI render metadata、file mutation queue、图片读取和 `.gitignore` 感知

这些取舍足以支撑一个真实可跑的编码 agent runtime，同时把复杂度控制在 phase 04 范围内。

### 4. Extension 模型

扩展点通过 `ICodingAgentExtension` 接口定义两个钩子：

- `ConfigureSessionAsync()`：在 session 创建前修改 prompt、context file、tool 集合
- `OnAgentEventAsync()`：观察底层 `AgentEvent`

`ExtensionLoader` 提供基于 Assembly 的动态加载能力：

- `LoadFromDirectory(path)`：扫描目录下所有 DLL，发现实现 `ICodingAgentExtension` 的类型并实例化
- `LoadFromAssembly(path)`：从单个 DLL 加载
- 使用独立 `AssemblyLoadContext` 实现隔离，跳过抽象类型和无参构造函数缺失的类型

### 5. 会话持久化 (SessionManager)

参考 pi-mono 的 `SessionManager`，实现 JSONL 格式的追加式持久化：

- **树结构**：每个 `SessionEntry` 有 `Id` + `ParentId`，形成 DAG；`LeafId` 跟踪当前位置
- **入口类型**：`SessionMessageEntry`（消息）、`ThinkingLevelChangeEntry`、`ModelChangeEntry`、`CompactionEntry`、`LabelEntry`
- **分支**：通过 `SetLeaf(id)` 将当前位置回退到任意历史节点，后续 `AppendEntry` 自动以该节点为 parent
- **上下文构建**：`BuildContext()` 沿 parent 链回溯，收集消息、thinking level、model 变更，并处理 compaction 边界（摘要 + 保留条目）
- **多态序列化**：使用 `[JsonPolymorphic]` + `[JsonDerivedType]` 实现 JSONL 中的类型鉴别

### 6. 上下文压缩 (CompactionService)

参考 pi-mono 的 `compaction/` 模块：

- **Token 估算**：`TokenEstimation` 使用 chars/4 启发式（保守高估），图片固定 1200 token
- **压缩判定**：`ShouldCompact(contextTokens, contextWindow, settings)` 在 token 超出 (contextWindow - reserveTokens) 时触发
- **裁剪点检测**：`FindCutPoint` 从最新消息倒推，累积 token 直到满足 `KeepRecentTokens`，只在 turn 边界（user/assistant）裁剪
- **摘要生成**：通过 `IChatClient` 调用 LLM 生成对话摘要
- **集成**：`TryCompactAsync` 组合判定 + 裁剪 + 摘要，返回 `CompactionEntry` 供 `SessionManager` 追加

### 7. 设置管理 (SettingsManager)

参考 pi-mono 的 `SettingsManager`：

- **两级设置**：全局 (`~/.pi-sharp/settings.json`) + 项目 (`.pi-sharp/settings.json`)，项目覆盖全局
- **深度合并**：`CodingAgentSettings.MergeWith()` 逐字段合并，overlay 非 null 字段覆盖 base
- **设置项**：`DefaultProvider`、`DefaultModel`、`DefaultThinkingLevel`、`Compaction`、`Retry` 等
- **持久化**：`FlushAsync()` 写入 JSON；`ReloadAsync()` 重新加载；`Create()` 从磁盘初始化
- **测试友好**：`InMemory()` 工厂方法，不依赖文件系统

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.CodingAgent |
|---|---|
| `buildSystemPrompt()` | `CodingAgentSystemPrompt.Build()` |
| `createAllToolDefinitions()` | `CodingAgentTools.CreateAll()` |
| `AgentSession` | `CodingAgentSession` |
| `SessionManager` | `SessionManager` (JSONL 持久化 + 树结构) |
| `compaction/` | `CompactionService` + `TokenEstimation` |
| `SettingsManager` | `SettingsManager` (全局 + 项目两级) |
| `extensions/loader.ts` | `ExtensionLoader` (Assembly 动态加载) |
| `Extension.configure()` | `ICodingAgentExtension.ConfigureSessionAsync()` |
| extension event hooks | `ICodingAgentExtension.OnAgentEventAsync()` |

## 当前取舍

- tools 先返回通用文本结果，不携带 pi-mono 那套 TUI 渲染细节
- `edit` 先实现单次精确替换 / `replaceAll`，不支持多段 edits 数组
- `read` 先支持文本文件，不支持图片和富 media
- cwd 保护基于路径规范化，不处理 symlink escape 这种更严格的沙箱语义
- `SessionManager` 存储简化的消息文本（role + text），不做完整 `ChatMessage` 多态序列化
- `ExtensionLoader` 基于 Assembly 加载，不支持 TypeScript 式的动态脚本加载
- `SettingsManager` 写入时全量覆盖，不做 pi-mono 的字段级修改追踪

## 验证

测试覆盖（40 个测试）：

- system prompt 对 tool/context/date/cwd 的拼装
- `write` → `read` → `edit` 的文件操作主链路
- `ls` / `find` / `grep` / `bash` 的基础行为
- path traversal 拦截
- extension 注入自定义 tool 和 prompt guideline
- `CodingAgentSession` 驱动 `Agent` 完成 tool call 闭环
- `SessionManager`：创建/追加/加载/分支/上下文构建/compaction 边界
- `TokenEstimation`：token 估算/压缩判定
- `CompactionService`：裁剪点检测/压缩触发
- `SettingsManager`：内存/磁盘/合并/全局-项目覆盖/持久化往返
- `ExtensionLoader`：Assembly 扫描/类型发现/跳过无效类型
