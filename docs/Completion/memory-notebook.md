# Completion & Completion.Abstractions — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `prototypes/Completion*` 的整体认知。
> **原则**：只记当前主线设计、已落地决策与高风险边界，不复述代码细节。
> **最后更新**：2026-05-02

---

## 一句话定位

**Completion.Abstractions** = 上层不可变契约：`ICompletionClient` + 请求/响应/工具/历史消息的 DTO。

**Completion** = provider 实现层：当前包含 Anthropic Messages 与 OpenAI Chat Completions 两套原生 client，加上若干通用工具类（JSON 参数解析、JsonSchema 生成）。

拆两层的目的：让 Agent.Core 等上层只依赖稳定的 Abstractions，新增 provider 时只动 Completion 层不破坏上层。

---

## 当前主线决策

### 1. 历史消息用 RL 术语（Observation/Action/ToolResults）而非 OpenAI 角色

`IHistoryMessage` 家族（在 Abstractions 层）：

- `ObservationMessage` — 来自环境/用户的输入
- `ActionMessage` — Agent/LLM 的输出；暴露 `Blocks: IReadOnlyList<ActionBlock>`，`GetFlattenedText()` 替代旧 `Content`
- `ToolResultsMessage` — 工具执行的结果集合

`ActionBlock` 是封闭 sum type（示例三子型：`Text` / `ToolCall` / `Thinking`），是 provider converter 与 Agent.Core 共享的有序内容语言。抱着换取不丢序列 / Thinking replay 能力的目的，`ActionBlock` / `ActionMessage` / `CompletionDescriptor` 都在 Abstractions 层，避免 Completion 反向依赖 Agent.Core。

`HistoryMessageKind` enum 给出统一标记。

转换到具体 provider 时（如 Anthropic 的 user/assistant 二分），由 provider 适配层处理：

- Observation + ToolResults → `role="user"`
- Action → `role="assistant"`
- 连续相同 role 的消息会被 `AnthropicMessageConverter.NormalizeMessageSequence()` 合并

> 这套抽象是为了未来 Agentic 多 Agent 场景预留——即"动作"和"观察"在概念上是不同的，不应被 provider 的 chat 角色绑死。

### 2. 对外统一返回 `Task<CompletionResult>`

`ICompletionClient.StreamCompletionAsync(request, observer, ct)` 对外返回一个完整的 `CompletionResult`。`observer` 为必传参数（显式传 `null` 即不观察）。

但 provider 内部仍然走流式 HTTP/SSE，解析出的增量按生成顺序喂给 `CompletionAggregator`，因此聚合后的 `Message.Blocks` 顺序仍等于 provider 实际生成顺序，不会乱序。

`CompletionResult` 暴露的核心结果：

- `Message.Blocks` — 有序 `ActionBlock[]`，其中包含 `Text / ToolCall / Thinking`
- `Invocation` — 本次调用的 `CompletionDescriptor` 指纹
- `Errors` — provider 通过错误事件上浮的错误文本，可为空

> 工具调用**不是**按 token 增量暴露给上层的——provider 内部累积完整 JSON 后才一次性 append 一个 `RawToolCall`。

> `RawArgumentsJson` 是 `content_block_stop` / `finish_reason=tool_calls` 时作为完整 JSON 文本一次性记录的，之后不再更新。

### 3. RawToolCall 只保留原始 arguments JSON 文本

- `RawArgumentsJson`：provider 收到的原始 JSON 字符串快照
- `Completion.Abstractions` 不再携带 schema-aware 解析结果
- 参数解析与错误/警告生成位于 `Agent.Core/Tool/ToolExecutor` 执行边界

这样设计是为了：

- 持久化时只存 provider 原始参数文本，重放时按当前工具定义重新解析
- history / replay 只保留“模型说了什么”，不混入“本地当前 schema 如何理解它”
- 调试时能看到 LLM 真实输出，而不是"被解析过的"版本

### 4. 工具参数解析采用 fallback 策略

`Agent.Core/Tool/JsonArgumentParser` 内部三层：

1. 原始 JSON 反序列化为 `Dictionary<string, JsonElement>`
2. 按 `ToolDefinition.Parameters[i].Type` 做类型转换（基本类型 / 枚举 / nullable）
3. 转换失败则降级为原值 + 收集 warning

错误（解析根本失败）和警告（个别字段类型不匹配）在执行边界通过 `ResolvedToolCall.ParseError` / `ParseWarning` 分离传播。

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

| 事件 | 动作 | 喂给聚合器的结果 |
|---|---|---|
| `message_start` | 消息生命周期开始 | — |
| `content_block_start` | 创建 ContentBlockState（区分 text / tool_use / thinking） | — |
| `content_block_delta` | 累积文本片段、input_json 片段、或 thinking/signature 片段 | `AppendContent(...)`（仅 text 时） |
| `content_block_stop` | tool_use 块完成 → 产出原始 arguments JSON；thinking 块完成 → 序列化为 `{type:thinking,thinking,signature}` JSON 字节 | `AppendToolCall(...)` 或 `AppendThinking(...)` |
| `message_delta` | 消息级增量（当前不向抽象层暴露额外元数据） | — |
| `message_stop` / `[DONE]` | 流结束 | — |
| `error` | 记录错误 | `AppendError(...)` |
| `ping` | 保活信号 | — |

JSON 解析失败会记 warning 但不中断流。

`OpenAIChatStreamParser` 则直接消费 chat/completions 的 `data: {...}` chunk：

| 字段 | 动作 | 喂给聚合器的结果 |
|---|---|---|
| `choices[].delta.content` | 追加正文增量 | `AppendContent(...)` |
| `choices[].delta.tool_calls[]` | 按 `tool_calls[i].index` 聚合 id/name/arguments 片段 | — |
| `choices[].finish_reason = "tool_calls"` | 将已聚合完成的调用一次性封装为 `RawToolCall` | `AppendToolCall(...)` |
| `reasoning_content` | `Strict` / `SgLangCompatible` 忽略；`DeepSeekV4` 捕获为 `OpenAIChatReasoningBlock`，并在 replay-compatible assistant history 中写回 | `OpenAIChatReasoningBlock` |
| `[DONE]` | 流结束；若仍有未刷出的工具调用则补刷 | — |

strict 路径默认保留所有 `delta.content`；只有特定 dialect（当前是 `SgLangCompatible`）才会忽略“工具调用已开始后夹带的纯空白 content noise”。

### 8. HTTP transport 能力通过 handler pipeline 统一挂载

- `Completion` 不重新发明新的 `HttpClient` facade
- capture / record / replay 主线统一落在 `Transport/*` 的 handler pipeline
- provider 继续只依赖普通 `HttpClient`
- MVP 以 text-first capture 为准：记录 `RequestText` / `ResponseText`；若 transport 在拿到 response 前失败，则追加 `ErrorText`
- response capture 必须保持流式消费语义，因此通过 tee read stream 在读取过程中复制文本，而不是预先整体 `ReadAsStringAsync()`
- replay 对请求仍做严格校验，但校验失败时不会消耗当前 golden log 条目
- `CompletionHttpTransportTests` 现已包含显式启用的本地 round-trip E2E：设置环境变量 `ATELIA_RUN_LOCAL_LLM_E2E=1` 后，会对 `http://localhost:8000/` 同时跑 OpenAI Chat 与 Anthropic Messages 的 record → JSONL → replay 闭环验证

---

## 核心数据流（一次 LLM 调用）

```text
1. Agent.Core 构造 CompletionRequest
   { ModelId, SystemPrompt, Context: IHistoryMessage[], Tools: ToolDefinition[] }

2. `await ICompletionClient.StreamCompletionAsync(request, null, ct)`
   → Provider 实现（AnthropicClient / OpenAIChatClient）：
      a. Provider-specific MessageConverter 把 IHistoryMessage[] → API messages
      b. JsonToolSchemaBuilder 把 ToolDefinition[] → provider tools (含 JsonSchema)
      c. 序列化后 POST 对应 endpoint（Anthropic: `v1/messages`; OpenAI Chat: `v1/chat/completions`）

3. HTTP 流接收
   ├─ ResponseHeadersRead 模式拿到持续开放的 Stream
   ├─ StreamReader 逐行读 SSE
   └─ "data: " 前缀过滤 → 抽出 JSON 行

4. 逐行 JsonNode.Parse → 分发到对应 provider 的 StreamParser
   ├─ text delta → `CompletionAggregator.AppendContent(...)`
   ├─ tool delta 聚合完成 → 形成 `RawToolCall` → `AppendToolCall(...)`
   ├─ thinking 块完成 → `AppendThinking(...)`


5. client 返回 `CompletionResult`；`Agent.Core` 再取 `result.Message` 包装成 `ActionEntry`
```

---

## 目录结构速览

```text
prototypes/Completion.Abstractions/
├─ ICompletionClient.cs       核心接口（Name, ApiSpecId, StreamCompletionAsync）
├─ CompletionRequest.cs       请求 DTO（不可变）
├─ ThinkingChunk.cs          thinking 块的 provider-neutral 容器
├─ IHistoryMessage.cs         消息家族 + HistoryMessageKind 枚举
├─ RawToolCall.cs             原始工具调用 + ToolExecutionStatus 枚举（Success/Failed/Skipped）
└─ ToolDefinition.cs          ToolDefinition + ToolParamSpec

prototypes/Completion/
├─ Transport/
│  ├─ CompletionHttpClientBuilder.cs   组装 capture / replay handler 链
│  ├─ CompletionHttpExchange.cs        HTTP 文本交换快照
│  ├─ ICompletionHttpExchangeSink.cs   capture sink 抽象 + 内存/调试实现
│  └─ ICompletionHttpReplayResponder.cs replay 抽象
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
├─ CompletionAggregator.cs          流式增量 → CompletionResult 的内部聚合器
└─ Utils/
   ├─ JsonArgumentParser.cs         工具参数 JSON → Dictionary
   ├─ JsonToolSchemaBuilder.cs      ToolParamSpec → JSON Schema
   ├─ StreamParserToolUtility.cs    StreamParser 共享工具
   └─ ToolArgumentParsingResult.cs  解析结果 DTO
```

---

## 已知边界与高风险点

### Provider 差异仍然明显

- 目前已有 Anthropic Messages 与 OpenAI Chat Completions 两套原生实现
- OpenAI 侧只覆盖 chat/completions，不含 Responses API
- 对于更广义的 OpenAI-compatible 服务，`finish_reason` 与 tool call 片段顺序仍可能存在方言差异
- 请求侧会严格校验 `assistant.tool_calls -> tool` 的相邻关系；若 `ToolResultsMessage` 缺少部分结果但提供了 `ExecuteError`，OpenAI converter 会按 pending `tool_call_id` 合成失败 `tool` 消息以维持协议合法性
- 当前只把已确认的高价值差异收敛进 `OpenAIChatDialect`，不做全量 profile 系统

### 工具参数表达力有限

- `ToolParamSpec` 主要面向扁平标量参数
- 嵌套对象 / 数组的 schema 表达不完整
- LLM JSON 没有 uint，调用方需自行做 long → uint 的范围检查

### 计费 token usage 已从 Completion 抽象层移除

- 当前项目不再在 `CompletionResult` 或 `ICompletionClient` 观察面暴露 provider 原始 token 计费字段
- 若未来真有成本分析需求，应基于实际场景重新设计，或直接对接 provider 的计费/查询 API
- `Agent.Core/History/ITokenEstimator.cs` 保留，其职责是跨 provider 的信息量估算，不等同于计费 token

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
- `observer` 参数为必传；不观察时显式传 `null`
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

- [HTTP Transport Pipeline](./http-transport-pipeline.md) — Completion 在 capture / record / replay 方向的主干设计

- [docs/Agent/memory-notebook.md](../Agent/memory-notebook.md) — Agent.Core 的对应概览
- [docs/LiveContextProto/AnthropicClient/](../LiveContextProto/AnthropicClient/) — 历史设计笔记，部分已被现行实现覆盖
- [docs/LiveContextProto/ToolFrameworkDesign.md](../LiveContextProto/ToolFrameworkDesign.md) — 工具框架早期讨论
