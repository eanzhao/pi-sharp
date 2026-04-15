# 01 - PiSharp.Ai 适配层

## 目标

`PiSharp.Ai` 是 `Microsoft.Extensions.AI` 之上的薄适配层，不重复实现 provider HTTP 客户端，而是补齐 `pi-mono/packages/ai` 在上层真正依赖的三类能力：

1. 细粒度事件流：把 MEAI 的 `ChatResponseUpdate` 转成 `TextStart` / `TextDelta` / `TextEnd` 等生命周期事件。
2. 扩展 usage：在 `UsageDetails` 基础上补充 cache write 和费用跟踪。
3. 注册表：用强类型 `ApiId` / `ProviderId` 和显式 registry 管理 provider 配置与模型元数据。

## 设计

### 事件模型

参考 `docs/00-project-overview.md` 的 phase 01 设计草图，`AssistantMessageEvent` 采用抽象 `record` + `sealed record` 派生类型：

- `TextStart` / `TextDelta` / `TextEnd`
- `ThinkingStart` / `ThinkingDelta` / `ThinkingEnd`
- `ToolCallStart` / `ToolCallDelta` / `ToolCallEnd`
- `Done`
- `Error`

这样后续 `PiSharp.Agent` 和 `PiSharp.Tui` 可以用 `switch` 模式匹配，不需要依赖字符串 tag。

### StreamAdapter

`StreamAdapter` 直接消费 `IAsyncEnumerable<ChatResponseUpdate>`，维护一个很小的状态机：

- 当前活动 block 类型：`text` / `thinking` / `tool call`
- 当前 block 的缓冲区
- 当前 tool call id
- 流末尾 finish reason
- 聚合后的 usage

规则如下：

1. 同类型内容连续到达时，只发 `Delta`。
2. 类型切换时，先关闭旧 block，再开启新 block。
3. `UsageContent` 不参与 block 切换，只用于累计 usage。
4. 流正常结束时，补发最后一个 `End`，然后发 `Done`。
5. 流异常结束时，补发最后一个 `End`，然后发 `Error`。

### Tool call delta 的取舍

MEAI 当前暴露的是 `FunctionCallContent.Arguments` 的结构化结果，不保证保留 provider 原始 JSON 增量片段。phase 01 先采用“快照求增量”的保守策略：

- 将当前参数字典序列化为 JSON 快照
- 如果新快照以前一个快照为前缀，则发后缀作为 delta
- 否则回退为发送整个新快照

这保证事件流是可消费的，但不是 provider 原始字节级 delta。后续如果某个 provider 需要精确保真，可再利用 `RawRepresentation` 做专门适配。

### 扩展 usage

MEAI `UsageDetails` 在 `10.4.1` 已包含：

- `InputTokenCount`
- `OutputTokenCount`
- `CachedInputTokenCount`
- `ReasoningTokenCount`

但仍没有 cache write 和费用字段，所以 `PiSharp.Ai` 提供 `ExtendedUsageDetails : UsageDetails`：

- `CacheWriteTokenCount`
- `UsageCostBreakdown Cost`

`CacheWriteTokenCount` 同时写入 `AdditionalCounts["pi-sharp.cache_write_tokens"]`，这样即使被当作 `UsageDetails` 传递，也不会完全丢失。

### Provider / Model 注册表

`pi-mono` 的 `api-registry.ts` 和 `models.ts` 主要做两件事：按 provider/api 查找能力，以及根据模型定价计算 usage 成本。C# 版本拆成两个 registry：

- `ProviderRegistry`
  - `ProviderConfiguration`
  - `ProviderRegistration`
  - 强类型 `ApiId` / `ProviderId`
- `ModelRegistry`
  - `ModelMetadata`
  - `ModelCapability`
  - `ModelPricing`

这层只做注册、查询、删除和成本计算，不在这里发起真实 provider 初始化。真实 `IChatClient` 的构造留给上层 CLI/DI 容器。

## 与 pi-mono 的对应关系

| pi-mono | PiSharp.Ai |
|---|---|
| `AssistantMessageEvent` union | `AssistantMessageEvent` 判别联合 |
| `api-registry.ts` | `ProviderRegistry` |
| `models.ts` | `ModelRegistry` + `UsageCostBreakdown` |
| provider stream output | `StreamAdapter.ToEvents()` |

## 当前范围和限制

- phase 01 只实现通用适配层，不实现任何具体 provider。
- `StreamAdapter` 目前只处理 `TextContent`、`TextReasoningContent`、`FunctionCallContent`、`UsageContent`。
- tool call delta 是“结构化快照增量”，不是 provider 原始流式片段。
- 模型注册表先支持手工注册，内置模型目录和自动生成表留到后续 phase。

## 验证

测试覆盖了：

- 文本流的 start/delta/end/done 生命周期
- thinking → text → tool call 的 block 切换
- 流中断后的 error 终止
- usage 成本计算
- provider/model registry 的注册与查询
