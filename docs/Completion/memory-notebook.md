# Completion & Completion.Abstractions — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `prototypes/Completion*` 的整体认知。
> **原则**：只记当前主线设计、已落地决策与高风险边界，不复述代码细节。
> **最后更新**：2026-04-23

---

## 一句话定位

**Completion.Abstractions** = 上层不可变契约：`ICompletionClient` + 请求/响应/工具/历史消息的 DTO。

**Completion** = 当前唯一的 provider 实现（Anthropic SSE 流式协议），加上若干通用工具类（JSON 参数解析、JsonSchema 生成）。

拆两层的目的：让 Agent.Core 等上层只依赖稳定的 Abstractions，新增 provider 时只动 Completion 层不破坏上层。

---

## 当前主线决策

### 1. 历史消息用 RL 术语（Observation/Action/ToolResults）而非 OpenAI 角色

`IHistoryMessage` 家族（在 Abstractions 层）：

- `ObservationMessage` — 来自环境/用户的输入
- `IActionMessage` — Agent/LLM 的输出；富接口子型 `IRichActionMessage` 额外暴露 `Blocks: IReadOnlyList<ActionBlock>`
- `ToolResultsMessage` — 工具执行的结果集合

`ActionBlock` 是封闭 sum type（示例三子型：`Text` / `ToolCall` / `Thinking`），是 provider converter 与 Agent.Core 共享的有序内容语言。抱着换取不丢序列 / Thinking replay 能力的目的，`ActionBlock` / `IRichActionMessage` / `CompletionDescriptor` 都在 Abstractions 层，避免 Completion 反向依赖 Agent.Core。

`HistoryMessageKind` enum 给出统一标记。

转换到具体 provider 时（如 Anthropic 的 user/assistant 二分），由 provider 适配层处理：

- Observation + ToolResults → `role="user"`
- Action → `role="assistant"`
- 连续相同 role 的消息会被 `AnthropicMessageConverter.NormalizeMessageSequence()` 合并

> 这套抽象是为了未来 Agentic 多 Agent 场景预留——即"动作"和"观察"在概念上是不同的，不应被 provider 的 chat 角色绑死。

### 2. 流式输出统一为 `IAsyncEnumerable<CompletionChunk>`

`ICompletionClient.StreamCompletionAsync(request, ct)` 永远返回 chunk 流，按生成顺序到达，**不会乱序**。

`CompletionChunk.Kind` 区分块类型：

- `Content` — 文本片段（增量）
- `ToolCall` — 一个完整的 `ParsedToolCall`（在 `content_block_stop` 时一次性产出）
- `Thinking` — `ThinkingChunk(OpaquePayload, PlainTextForDebug?)`；parser 在 `content_block_stop` 时将 provider-native 序列化字节一次性产出，Agent.Core / Accumulator 不解释该字节内容
- `Error` — 流中错误
- `TokenUsage` — 累计统计；由 `message_start` / `message_delta` 中的 usage 字段逐步累积，流结束时由上层 `CompletionAccumulator` 一次性 yield

> 工具调用**不是**按 token 增量到达上层的——provider 内部累积完整 JSON 后才 yield 一次。但累积过程对 Agent.Core 透明。

> `RawArguments` 是 `content_block_stop` 时作为完整 JSON 文本一次性记录的，之后不再更新。

### 3. ParsedToolCall 同时保留 RawArguments 和 Arguments

- `RawArguments`：provider 收到的原始 JSON 字符串快照
- `Arguments`：经过 schema-aware 解析的强类型 `Dictionary<string, object?>`
- 解析失败时 `Arguments` 可能不完整，但 `RawArguments` 始终保留

这样设计是为了：

- 持久化时可以只存 RawArguments，重放时再解析
- 解析失败可降级到原始字符串呈现给上层做容错
- 调试时能看到 LLM 真实输出，而不是"被解析过的"版本

### 4. 工具参数解析采用 fallback 策略

`JsonArgumentParser` 内部三层：

1. 原始 JSON 反序列化为 `Dictionary<string, JsonElement>`
2. 按 `ToolDefinition.Parameters[i].Type` 做类型转换（基本类型 / 枚举 / nullable）
3. 转换失败则降级为原值 + 收集 warning

错误（解析根本失败）和警告（个别字段类型不匹配）通过 `ParsedToolCall.Errors` / `Warnings` 分离传播。

### 5. JsonSchema 由 ToolParamSpec 单方向生成

`JsonToolSchemaBuilder.BuildToolInputSchema(...)` 把 `ToolDefinition.Parameters[]` 转成 Anthropic / OpenAI 通用的 JSON Schema。

- 当前不支持反向（schema → ToolParamSpec）
- 嵌套对象/数组的 schema 表达有限，主要面向"扁平的标量参数"
- 默认值、可空性、enum 通过 spec 字段直接表达

### 6. 当前有两套 provider：Anthropic Messages + OpenAI Chat Completions

- `LiveContextProto/Program.cs` 当前实验入口默认走 `OpenAIChatClient(OpenAIChatDialects.SgLangCompatible)`，并使用 `http://localhost:8000/`
- 同一台本地 sglang 服务也暴露了 OpenAI-compatible `POST /v1/chat/completions`
- `Completion` 现已包含原生 `OpenAIChatClient`，面向 chat/completions 流式工具调用（不是 Responses API）
- OpenAI-compatible 的长期演进遵循 “Strict Core + Quirk Modules”，细则见 `docs/Completion/openai-compatible-evolution.md`

### 7. SSE 解析是手写状态机，不依赖通用 SSE 库

`AnthropicStreamParser` 直接处理 8 种事件：

| 事件 | 动作 | 输出 chunk |
|---|---|---|
| `message_start` | 初始化 usage 累计 | — |
| `content_block_start` | 创建 ContentBlockState（区分 text / tool_use / thinking） | — |
| `content_block_delta` | 累积文本片段、input_json 片段、或 thinking/signature 片段 | Content（仅 text 时） |
| `content_block_stop` | tool_use 块完成 → 解析参数；thinking 块完成 → 序列化为 `{type:thinking,thinking,signature}` JSON 字节 | ToolCall 或 Thinking |
| `message_delta` | 更新累积 usage | — |
| `message_stop` / `[DONE]` | 流结束（上层 Accumulator 在此后一次性 yield TokenUsage） | — |
| `error` | 记录错误 | Error |
| `ping` | 保活信号 | — |

JSON 解析失败会记 warning 但不中断流。

`OpenAIChatStreamParser` 则直接消费 chat/completions 的 `data: {...}` chunk：

| 字段 | 动作 | 输出 chunk |
|---|---|---|
| `choices[].delta.content` | 追加正文增量 | Content |
| `choices[].delta.tool_calls[]` | 按 `tool_calls[i].index` 聚合 id/name/arguments 片段 | — |
| `choices[].finish_reason = "tool_calls"` | 将已聚合完成的调用一次性解析为 `ParsedToolCall` | ToolCall |
| `usage` | 更新累计 token 统计 | — |
| `reasoning_content` | 忽略，不上浮到抽象层 | — |
| `[DONE]` | 流结束；若仍有未刷出的工具调用则补刷 | — |

strict 路径默认保留所有 `delta.content`；只有特定 dialect（当前是 `SgLangCompatible`）才会忽略“工具调用已开始后夹带的纯空白 content noise”。

---

## 核心数据流（一次 LLM 调用）

```text
1. Agent.Core 构造 CompletionRequest
   { ModelId, SystemPrompt, Context: IHistoryMessage[], Tools: ToolDefinition[] }

2. ICompletionClient.StreamCompletionAsync(request)
   → Provider 实现（AnthropicClient / OpenAIChatClient）：
      a. Provider-specific MessageConverter 把 IHistoryMessage[] → API messages
      b. JsonToolSchemaBuilder 把 ToolDefinition[] → provider tools (含 JsonSchema)
      c. 序列化后 POST 对应 endpoint（Anthropic: `v1/messages`; OpenAI Chat: `v1/chat/completions`）

3. HTTP 流接收
   ├─ ResponseHeadersRead 模式拿到持续开放的 Stream
   ├─ StreamReader 逐行读 SSE
   └─ "data: " 前缀过滤 → 抽出 JSON 行

4. 逐行 JsonNode.Parse → 分发到对应 provider 的 StreamParser
   ├─ text delta → yield Content chunk
   ├─ tool delta 聚合完成 → JsonArgumentParser.Parse → yield ToolCall chunk
   └─ usage delta → 更新 usage（最后 yield TokenUsage）

5. 上层（CompletionAccumulator）逐块消费，组装成 ActionEntry
```

---

## 目录结构速览

```text
prototypes/Completion.Abstractions/
├─ ICompletionClient.cs       核心接口（Name, ApiSpecId, StreamCompletionAsync）
├─ CompletionRequest.cs       请求 DTO（不可变）
├─ CompletionChunk.cs         流式块 + Kind 枚举
├─ IHistoryMessage.cs         消息家族 + HistoryMessageKind 枚举
├─ ParsedToolCall.cs          工具调用 + ToolExecutionStatus 枚举（Success/Failed/Skipped）
└─ ToolDefinition.cs          ToolDefinition + ToolParamSpec

prototypes/Completion/
├─ Anthropic/
│  ├─ AnthropicClient.cs            ICompletionClient 实现
│  ├─ AnthropicApiModels.cs         请求/响应 DTO（snake_case 序列化）
│  ├─ AnthropicMessageConverter.cs  IHistoryMessage[] → AnthropicMessage[]
│  └─ AnthropicStreamParser.cs      SSE 事件状态机
├─ OpenAI/
│  ├─ OpenAIChatClient.cs           ICompletionClient 实现
│  ├─ OpenAIChatApiModels.cs        chat/completions 请求 DTO
│  ├─ OpenAIChatDialect.cs          轻量 dialect（已知兼容差异的组合）
│  ├─ OpenAIChatMessageConverter.cs IHistoryMessage[] → OpenAI messages[]
│  └─ OpenAIChatStreamParser.cs     SSE delta 聚合状态机
└─ Utils/
   ├─ JsonArgumentParser.cs         工具参数 JSON → Dictionary
   ├─ JsonToolSchemaBuilder.cs      ToolParamSpec → JSON Schema
   └─ ToolArgumentParsingResult.cs  解析结果 DTO
```

---

## 已知边界与高风险点

### Provider 差异仍然明显

- 目前已有 Anthropic Messages 与 OpenAI Chat Completions 两套原生实现
- OpenAI 侧只覆盖 chat/completions，不含 Responses API
- 对于更广义的 OpenAI-compatible 服务，`finish_reason`、`usage`、tool call 片段顺序仍可能存在方言差异
- 请求侧会严格校验 `assistant.tool_calls -> tool` 的相邻关系；若 `ToolResultsMessage` 缺少部分结果但提供了 `ExecuteError`，OpenAI converter 会按 pending `tool_call_id` 合成失败 `tool` 消息以维持协议合法性
- 当前只把已确认的高价值差异收敛进 `OpenAIChatDialect`，不做全量 profile 系统

### 工具参数表达力有限

- `ToolParamSpec` 主要面向扁平标量参数
- 嵌套对象 / 数组的 schema 表达不完整
- LLM JSON 没有 uint，调用方需自行做 long → uint 的范围检查

### 缓存语义跨 provider 不统一

- Anthropic 的 cache_read / cache_creation tokens 在 `TokenUsage` 中已暴露
- 但 OpenAI / 其他 provider 的缓存语义不同，目前没有抽象层
- 跨 provider 的统一缓存策略待设计

### 消息序列化向后兼容性

- `IHistoryMessage` 没有版本字段
- 集成 StateJournal 持久化时，需要先在 Abstractions 加 schema discriminator / version
- 当前没有任何序列化代码路径（消息只在内存里流转）

### 流式参数到达不能 early validate

- ToolCall 的 JSON 是流式 input_json_delta 累积
- 必须等 `content_block_stop` 才能完整解析
- 如果 LLM 一直输出非法 JSON，要等整块结束才能报错

### Turn 锁定约束在 Agent.Core 执行

- Completion 层不感知 "Turn"概念；`ICompletionClient.StreamCompletionAsync` 是无状态的
- Turn 内 `LlmProfile` 锁定的不变量与校验全部位于 `Agent.Core/AgentEngine.cs`
- 详见 [docs/Agent/memory-notebook.md](../Agent/memory-notebook.md) 的"Turn 与 LlmProfile 锁定"小节
- 该约束为 thinking/reasoning replay 奠定前提：`ActionBlock.Thinking.Origin` 与 Turn lock 同构，`ProjectInvocationContext` 仅在 `Origin == TargetInvocation` 且存在显式 Turn 起点时在 ActiveTurnTail 保留 thinking，Stable Prefix 始终剩离。Anthropic 路径已端到端落地；OpenAI reasoning_content 仍丢弃。

---

## 与邻居项目的关系

```text
Agent.Core (推理循环)
    ↓ depends on
Completion.Abstractions (本项目)
    ↑ implements
Completion (本项目，含 Anthropic / OpenAI Chat)
    ↓ HTTP
Anthropic API / OpenAI-compatible API / 本地 sglang 服务
```

- Abstractions 不依赖任何 provider DLL
- Completion 当前包含 Anthropic 与 OpenAI Chat 两组实现，分层仍允许继续扩展 provider
- `LlmProfile`（在 Agent.Core）= `ICompletionClient + ModelId + 显示名` 的捆绑

---

## 调研中发现的设计悬而未决处

1. **多 provider 的统一 token 计数**：Anthropic 有详细 cache 字段，OpenAI 有不同口径，跨 provider 抽象未做
2. **消息持久化的 schema 演进**：`IHistoryMessage` 是 interface 而非 sealed 类层次，序列化兼容性策略未定

> 以下几项在调研过程中发现**代码中已经落地**，列在此处供后续读者快速校准：
>
> - **工具执行的归属**：完全在 Agent.Core `ToolExecutor` 中执行，Completion 层只负责解析参数
> - **OpenAI 原生支持时机**：代码中已落地 `OpenAIChatClient`，但范围明确限制在 chat/completions；Responses API 仍未做

---

## 相关文档

- [docs/Agent/memory-notebook.md](../Agent/memory-notebook.md) — Agent.Core 的对应概览
- [docs/LiveContextProto/AnthropicClient/](../LiveContextProto/AnthropicClient/) — 历史设计笔记，部分已被现行实现覆盖
- [docs/LiveContextProto/ToolFrameworkDesign.md](../LiveContextProto/ToolFrameworkDesign.md) — 工具框架早期讨论
