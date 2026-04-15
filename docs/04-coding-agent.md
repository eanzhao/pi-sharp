# 04 - PiSharp.CodingAgent

## 目标

Phase 04 实现 `PiSharp.CodingAgent` 的最小核心层，承接 `PiSharp.Agent`，为后续 CLI 提供一个可复用的“编码会话运行时”：

1. 默认 system prompt 构建
2. 内置 coding tools
3. 有状态 session wrapper
4. 最小 extension hook

本阶段先把编码智能体最核心的运行时闭环搭起来，不尝试一次性复刻 pi-mono 的 compaction、session tree、
interactive mode 和完整 extension UI。

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
- xUnit 测试覆盖 prompt、tools、extension、session 主链路

## 设计

### 1. Session 是 CodingAgent 的主入口

参考 pi-mono 的 `AgentSession`，C# 版本提供 `CodingAgentSession` 作为高层状态封装：

- 内部持有一个 `PiSharp.Agent.Agent`
- 暴露 `PromptAsync()` / `ContinueAsync()` / `Steer()` / `FollowUp()`
- 对外保留 `AgentState`
- 负责把 prompt、tool 集合、extension 配置收敛成一个完整 agent runtime

当前版本先只做**内存内会话管理**。磁盘持久化、branching 和 compaction 留到更上层阶段。

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

当前只保留两个最重要的扩展点：

- `ConfigureSessionAsync()`：在 session 创建前修改 prompt、context file、tool 集合
- `OnAgentEventAsync()`：观察底层 `AgentEvent`

这比 pi-mono 的完整 extension API 小很多，但已经能覆盖 phase 05 最关键的能力：

- 注册自定义工具
- 注入额外规则
- 监听 tool call / assistant streaming / turn 生命周期

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.CodingAgent |
|---|---|
| `buildSystemPrompt()` | `CodingAgentSystemPrompt.Build()` |
| `createAllToolDefinitions()` | `CodingAgentTools.CreateAll()` |
| `AgentSession` | `CodingAgentSession` |
| `Extension.configure()` | `ICodingAgentExtension.ConfigureSessionAsync()` |
| extension event hooks | `ICodingAgentExtension.OnAgentEventAsync()` |

## 当前取舍

- session 只做内存态 orchestration，不做 JSONL 持久化和树状历史
- tools 先返回通用文本结果，不携带 pi-mono 那套 TUI 渲染细节
- `edit` 先实现单次精确替换 / `replaceAll`，不支持多段 edits 数组
- `read` 先支持文本文件，不支持图片和富 media
- cwd 保护基于路径规范化，不处理 symlink escape 这种更严格的沙箱语义

## 验证

测试覆盖：

- system prompt 对 tool/context/date/cwd 的拼装
- `write` → `read` → `edit` 的文件操作主链路
- `ls` / `find` / `grep` / `bash` 的基础行为
- path traversal 拦截
- extension 注入自定义 tool 和 prompt guideline
- `CodingAgentSession` 驱动 `Agent` 完成 tool call 闭环
