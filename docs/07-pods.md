# 07 - PiSharp.Pods

## 目标

Phase 07 实现 `PiSharp.Pods` 的最小核心层，把 pi-mono `pods` 包里**稳定、纯本地、可测试**的部分先落到 C#：

1. 本地 pod 配置读写（`~/.pi/pods.json` / `PI_CONFIG_DIR`）
2. 已知模型目录与 GPU 兼容性匹配
3. 模型部署规划（端口、GPU、vLLM 参数、环境变量）
4. SSH 驱动的 setup/start/stop/list/logs orchestration
5. 基于 `PiSharp.Agent` 的 OpenAI-compatible prompt runtime

这一阶段先把可用闭环落下来：

- `PiSharp.Pods` 里提供可测试的 orchestration/service 层
- `PiSharp.Pods.Cli` 作为极薄的终端入口
- `PiSharp.Cli` 通过 `pods` 命名空间转发到 `PiSharp.Pods`

也就是说，这一步已经不是“只做纯本地规划库”，而是把真正能跑起来的 pod 管理主链路接通了。

## 参考实现

- `pi-mono/packages/pods/src/types.ts`
- `pi-mono/packages/pods/src/config.ts`
- `pi-mono/packages/pods/src/model-configs.ts`
- `pi-mono/packages/pods/src/commands/models.ts`
- `pi-mono/packages/pods/src/commands/prompt.ts`
- `pi-mono/packages/pods/src/models.json`

## 本阶段范围

- `PiSharp.Pods.csproj`
- `PodsConfigurationStore`
- `KnownModelCatalog`
- `PodDeploymentPlanner`
- `IPodSshTransport` / `ProcessPodSshTransport`
- `PodService`
- `PodEndpointResolver`
- `PodPromptSystemPrompt`
- `PodPromptTools`
- `PodAgentFactory`
- `PodsApplication`
- `PiSharp.Pods.Cli`
- `PiSharp.Pods.Tests`

## 设计

### 1. 先做 library，再补一个极薄 CLI 壳

TS 版 `pi` 是一个命令行程序，但仓库里已经有 `PiSharp.Cli` 负责 coding-agent 主入口。phase 07 先提炼 `Pods` 的核心库：

- 配置状态管理
- 已知模型目录
- 纯函数式部署规划
- 连接 OpenAI-compatible endpoint 的 agent runtime
- SSH 驱动的远程编排

在这个基础上，再补一个非常薄的 `PiSharp.Pods.Cli`：

- `Program.cs` 只负责把 args 转给 `PodsApplication`
- 实际参数路由、输出和错误处理都留在 `PiSharp.Pods`

在此之上，主 `PiSharp.Cli` 再把 `pi pods ...` 这一整段命名空间转发给 `PiSharp.Pods`，而不是复制一份参数解析和命令实现。

这样 phase 07 既保留可复用核心，也交付了两个入口：

- 独立的 `PiSharp.Pods.Cli`
- 主 CLI 下的 `pi pods ...`

这里特意没有把 `start/stop/list/logs/agent` 这些裸命令直接挂到 `PiSharp.Cli` 根上，因为当前 `PiSharp.Cli` 默认行为是把剩余参数当成 coding-agent prompt。
如果抢占这些裸词，就会和诸如 `pi start fixing tests` 这样的自然语言输入发生冲突。

### 2. 本地 JSON 状态沿用 pi-mono 的 schema

`PodsConfigurationStore` 继续使用 `pods.json` 这套本地状态模型：

- `pods[name].ssh`
- `pods[name].gpus`
- `pods[name].models`
- `active`

同时保留 `PI_CONFIG_DIR` 覆盖默认目录的行为，方便测试和多环境隔离。

为了让调用方拿到稳定结构，store 在加载后会做一次 normalize：

- 空字典补默认值
- `vllmVersion` 缺省时回落到 `release`
- `active` 指向不存在 pod 时自动清空

### 3. 已知模型目录作为嵌入资源

`models.json` 直接沿用参考实现，并作为 embedded resource 打进 `PiSharp.Pods.dll`。

`KnownModelCatalog` 提供三类能力：

- 判断模型是否在已知目录里
- 读取展示名和可用 GPU count
- 按 `gpuCount + gpuType` 选择最佳配置

匹配逻辑与 TS 版保持一致：

- 先找 GPU 数量和 GPU 类型都匹配的配置
- 找不到时，再退化到只按 GPU 数量匹配

### 4. 把“规划”从“执行”里拆出来

`commands/models.ts` 里混在一起的端口分配、GPU 选择、已知模型匹配、参数覆盖逻辑，在 C# 版拆成 `PodDeploymentPlanner`：

- `GetNextPort()`：从 `8001` 开始找下一个可用端口
- `SelectGpus()`：按当前部署占用次数做 least-used 选择
- `Plan()`：输出 `ModelDeploymentPlan`

这样 phase 07 就能把最容易出错的部分用单元测试锁住，而把真实 SSH `start/stop/logs` 留到更上层实现。

### 5. SSH orchestration 通过传输抽象接入

在规划层之上，C# 版新增：

- `IPodSshTransport`
- `ProcessPodSshTransport`
- `PodScriptAssets`
- `PodService`

对应关系大致是：

- `ssh.ts` → `IPodSshTransport` / `ProcessPodSshTransport`
- `commands/pods.ts` → `PodService.SetupPodAsync()` / `SetActivePod()` / `RemovePod()`
- `commands/models.ts` → `PodService.StartModelAsync()` / `StopModelAsync()` / `StopAllModelsAsync()` / `GetModelStatusesAsync()` / `StreamLogsAsync()`

脚本本体继续直接复用参考实现的 `pod_setup.sh` 和 `model_run.sh`，作为 embedded resource 打包到程序集里。
运行时通过 SSH heredoc 上传到远端 `/tmp` 后执行。

这样比把脚本逻辑重写成 C# 字符串更稳，也更贴近 TS 版本的实际行为。

### 6. Prompt runtime 直接复用 Agent

参考 `prompt.ts`，`PiSharp.Pods` 需要一个“快速验证远端模型”的入口。但在 C# 版，不单独再包一层自定义 agent runtime，而是直接复用 `PiSharp.Agent.Agent`：

- `PodEndpointResolver`：从配置里解析部署对应的 `BaseUri`
- `PodPromptSystemPrompt`：生成代码导航 prompt
- `PodPromptTools`：提供 `ls` / `read` / `glob` / `rg`
- `PodAgentFactory`：把 endpoint、tools、system prompt 组装成 `Agent`

这里没有依赖 `PiSharp.CodingAgent`，是有意为之：

- 原始依赖图里 `pods` 只依赖 `agent`
- phase 07 只需要轻量的“模型 smoke test / code navigation”能力
- 不需要把完整 coding session、编辑工具和扩展系统带进来

### 7. 命令路由保持手写、小而稳

`PodsApplication` 没有额外引入 System.CommandLine，而是沿用仓库前面几个 phase 的思路，手写一个小型路由器：

- `pods`
- `setup`
- `active`
- `remove`
- `start`
- `stop`
- `list`
- `doctor`
- `logs`
- `agent`

这样一来它既能服务独立的 `PiSharp.Pods.Cli`，也能服务主 CLI 下的 `pi pods ...` 转发。

这样做的理由和 phase 05 一致：

- 当前命令面还不算大
- 参数约束很明确
- 单元测试更直接
- 后面如果要再接 `ssh` / `shell` 这类远端命令，也不会受框架结构限制

### 8. gpt-oss 走 Responses，其它模型走 Chat Completions

TS 版 `prompt.ts` 明确区分了 gpt-oss 模型要走 `/v1/responses`。C# 版也保留这点：

- 普通模型：`OpenAI.Chat.ChatClient`
- `gpt-oss*`：`OpenAI.Responses.ResponsesClient`

然后统一通过 `Microsoft.Extensions.AI.OpenAI` 的 `AsIChatClient()` 适配成 `IChatClient`，供 `Agent` 使用。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.Pods |
|---|---|
| `types.ts` | `PodTypes.cs` |
| `config.ts` | `PodsConfigurationStore` |
| `model-configs.ts` | `KnownModelCatalog` |
| `commands/models.ts` 中的规划逻辑 | `PodDeploymentPlanner` |
| `ssh.ts` | `IPodSshTransport` / `ProcessPodSshTransport` |
| `commands/pods.ts` / `commands/models.ts` | `PodService` |
| `commands/prompt.ts` | `PodEndpointResolver` + `PodPromptSystemPrompt` + `PodAgentFactory` |
| `prompt/tools.ts` 草图 | `PodPromptTools` |
| `cli.ts` | `PodsApplication` + `PiSharp.Pods.Cli` |

## 当前取舍

- `ssh` / `shell` 提供最小可用远端访问，但还没有更高阶的会话管理或本地增强体验
- `ssh` 现在支持 `-t` / `--tty`，便于运行需要伪终端的远端命令，但还没有额外的本地 session 管理
- `agent` 目前支持 print-mode prompt，也支持 `-i` / 无 prompt 进入交互模式
- `start` 支持 `--detach`，可以只做远端启动并立即返回；启动健康确认仍然默认基于日志关键字和 `/health`
- `list` 支持 `--no-verify` 跳过 SSH health check，适合配置离线检查；默认仍会远端校验状态
- `doctor` 提供 SSH、GPU、models path、venv/vLLM、日志目录和 deployment health 的结构化诊断，但还没有自动修复或更复杂的远端基线管理
- `logs` 支持 `--tail` / `--no-follow`，但还没有更复杂的日志过滤、grep 或多部署聚合视图
- `rg` 先实现为内置 regex 搜索，而不是直接包装系统 `rg`
- `glob` 先支持常见 `*` / `**` / `?` 语义，不追求完整 shell glob 行为
- 启动监控先基于日志关键字和 `/health` 探测，不做更复杂的 remote supervisor
- 主 CLI 只开放 `pi pods ...` 命名空间，不抢占 `pi start` / `pi stop` / `pi agent` 这类会和 coding-agent prompt 冲突的裸命令

这些取舍让 phase 07 已经具备真实可跑的 pod 管理闭环，但仍把更重的交互式能力控制在后续阶段。

## 验证

测试覆盖：

- `pods.json` 的默认加载、round-trip 和 active pod 更新
- 已知模型目录加载、GPU 类型优先匹配、可用 GPU count 枚举
- 部署规划的端口选择、least-used GPU 选择、memory/context 覆盖
- `PodService` 的 setup / start / failed-start rollback 行为
- `PodsApplication` 的 standalone / namespaced 路由
- `PiSharp.Cli` 到 `PiSharp.Pods` 的 `pods` 命名空间转发
- `pods doctor` 的健康检查与返回码
- `pods ssh` / `pods shell` 的远端命令、TTY 模式与 shell 启动
- `pods start --detach`、`pods list --no-verify`、`pods logs --tail/--no-follow` 的 CLI 行为
- `pods agent` 的 print / interactive 双路径
- endpoint 解析和 gpt-oss 的 API 类型选择
- `ls` / `read` / `glob` / `rg` 工具的基础行为与路径逃逸拦截
