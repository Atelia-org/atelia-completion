# Atelia.Completion — 快速上手（面向使用者）

> **读者**：要在自己的代码里通过 `Atelia.Completion[.Abstractions]` 调用 LLM 的高级 LLM Agent / 上层应用作者。
> **不读这份**：要给 Completion 层加新 provider、改 SSE 解析的人——请去 [memory-notebook.md](./memory-notebook.md) 与 [openai-compatible-evolution.md](./openai-compatible-evolution.md)。
> **配套环境**：本机 `http://localhost:8000/` 上有 sglang 服务，同时暴露 Anthropic Messages (`/v1/messages`) 与 OpenAI Chat (`/v1/chat/completions`) 两类端点。
> **最后更新**：2026-04-30

---

## 阅读路径

不同诉求挑不同章节，避免一次性吞噬所有信息：

- **只调文本**（hello-world）：§1 → §2 → §4.1 → §5 → §6
- **加上工具调用**：再读 §3.1、§3.2、§4.3
- **多轮回灌历史 / 用 thinking 模型**：再读 §4.2、§4.4
- **撞到本地服务报错**：直接跳 §6 故障速查

---

## 1. 心智模型

两个 csproj，对应两层关注点：

| 程序集 | 命名空间 | 你需要的核心符号 |
|---|---|---|
| `Atelia.Completion.Abstractions` | `Atelia.Completion.Abstractions` | `ICompletionClient`、`CompletionRequest`、`IHistoryMessage` 家族（含 `ActionMessage` / `ActionBlock`）、`ToolDefinition` / `ToolParamSpec`、`CompletionDescriptor`、`CompletionResult`、`ThinkingChunk` |
| `Atelia.Completion` | `Atelia.Completion.Anthropic`、`Atelia.Completion.OpenAI` | `AnthropicClient`、`OpenAIChatClient`、`OpenAIChatDialects`（静态访问点：`.Strict` / `.SgLangCompatible`） |

**只引 Abstractions** 用来定义请求/历史/工具——provider-neutral，不会拉出 HttpClient。
**再引 Completion** 才能拿到具体 client 实现。

一次 LLM 调用永远是这条三步曲：

```
build CompletionRequest  →  await client.StreamCompletionAsync(...)  →  CompletionResult
```

`ICompletionClient` 只有 **一个方法**——发起流式补全，并在 client 内部完成聚合：

```csharp
Task<CompletionResult> StreamCompletionAsync(
    CompletionRequest request,
    CompletionStreamObserver? observer,
    CancellationToken cancellationToken = default
);
```

`observer` 为必传参数——不需要流式观察时显式传 `null` 即可。没有单独暴露的"增量 chunk 流"公共接口。provider 仍然走流式 HTTP/SSE，但由 client 内部解析并聚合后再返回 `CompletionResult`。

---

## 2. 三十秒上手（本地 sglang，OpenAI 路径）

**复制前自检**：

- 项目引用：`Atelia.Completion.Abstractions` + `Atelia.Completion`
- 必备 using：见示例顶部
- 本地 sglang 已经监听 8000，模型名按本机加载情况替换 `ModelId`
- 本地 sglang 必须用 `OpenAIChatDialects.SgLangCompatible`（详见 §5.1）

```csharp
using System.Collections.Immutable;
using System.Threading;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

var client = new OpenAIChatClient(
    apiKey: null,                                   // 本地服务通常无需 key
    baseAddress: new Uri("http://localhost:8000/"), // 注意结尾的 '/'
    dialect: OpenAIChatDialects.SgLangCompatible    // 本地 sglang 必须选这个
);

var request = new CompletionRequest(
    ModelId: "Qwen3.5-27b-GPTQ-Int4",
    SystemPrompt: "You are a helpful assistant.",
    Context: new IHistoryMessage[] {
        new ObservationMessage("用一句话介绍自己。"),
    },
    Tools: ImmutableArray<ToolDefinition>.Empty
);

var ct = CancellationToken.None;                    // 生产代码请传真正的 token
var result = await client.StreamCompletionAsync(request, null, ct);

Console.WriteLine(result.Message.GetFlattenedText());
foreach (var call in result.Message.ToolCalls) { /* ... */ }
foreach (var err in result.Errors ?? Array.Empty<string>()) { /* ... */ }
```

**Anthropic 路径** 调用形态对称（构造 client 后 `CompletionRequest` 与 `await client.StreamCompletionAsync(request, null, ct)` 写法完全一致）：

```csharp
using Atelia.Completion.Anthropic;

var anthropic = new AnthropicClient(
    apiKey: null,
    baseAddress: new Uri("http://localhost:8000/")
);
```

> **跨 provider 复用 `CompletionRequest` 的边界**：纯文本 + 工具调用历史可以原样喂给任一 client，provider 差异封装在内部 converter / parser 里。**但** 含 `ActionBlock.Thinking` 的回灌历史 **不可以跨 provider 也不可以跨调用源**——`OpaquePayload` 是 provider-native 字节，`Origin` 必须是当时产出它的那次调用（详见 §4.4）。

---

## 3. 构造 `CompletionRequest`

```csharp
public sealed record CompletionRequest(
    string ModelId,                                 // 必填，且必须能被服务端识别
    string SystemPrompt,                            // 空字符串可以，按约定不要传 null
    IReadOnlyList<IHistoryMessage> Context,         // 历史消息，按时间顺序
    ImmutableArray<ToolDefinition> Tools            // 无工具时传 ImmutableArray<ToolDefinition>.Empty —— 不要用 default！
);
```

四个字段都不可变。要"修改"一次请求，新建一个 record（用 `with`）。

**当前不暴露的字段**（按需走 provider 默认值，未来再扩展）：`temperature`、`top_p`、`max_tokens`、`stop` 等采样参数；`tool_choice` / 强制调用某工具；`response_format` 等。

⚠️ **不要把 system 拼进 `Context`**——它走独立的 `SystemPrompt` 字段，与历史解耦。

⚠️ **`Tools` 必须显式构造**：用 `ImmutableArray.Create(...)` 或 `.Empty`。`default(ImmutableArray<T>)` 的 `IsDefault == true`，converter 内部 `foreach` 时会 NRE。

### 3.1 `Context`：用 RL 术语，不是 OpenAI 角色

`IHistoryMessage` 故意 **不叫** `user/assistant/system/tool`——它向强化学习靠拢，为多 Agent 场景预留。映射规则：

| 你想表达 | 用什么 | 备注 |
|---|---|---|
| 用户输入 / 系统通知 / 环境观测 | `new ObservationMessage(string? content)` | 统一文本字段 |
| LLM 上一次输出（要回灌） | `CompletionResult.Message`（取聚合结果的 `ActionMessage` 字段，详见 §4.2） | 纯 `ActionMessage` 实现 `IHistoryMessage`，可塞回 `Context` |
| 工具执行结果（要回灌） | `new ToolResultsMessage(content, results, executeError)` | `results: IReadOnlyList<ToolResult>`，每条带 `ToolCallId` |

**`ActionMessage` 的归属**：`ActionBlock` sum type、`CompletionDescriptor`、`ActionMessage` 都在 **Abstractions** 层。多轮回灌的标准写法：取 `CompletionResult.Message` 即可——它是 `ActionMessage`（实现 `IHistoryMessage`），可直接 `history.Add(result.Message)`（见 §4.2）。

（`ActionMessage` 已经提供了 `GetFlattenedText()` / `ToolCalls` / `Blocks`，直接用它即可。）

最小可用历史：

```csharp
var history = new List<IHistoryMessage> {
    new ObservationMessage("帮我把 today.md 里的 TODO 抽出来。"),
};
```

带工具回灌的多轮：

```csharp
var history = new List<IHistoryMessage> {
    new ObservationMessage("帮我读一下 README。"),                  // Turn 1 input
    previousAction,                                                  // Turn 1 LLM 输出（ActionMessage）
    new ToolResultsMessage(
        Content: null,
        Results: new[] {
            new ToolResult(
                ToolName: "fs.read",
                ToolCallId: "call_abc123",                           // 必须与上一步 ActionBlock.ToolCall 的 Id 对齐
                Status: ToolExecutionStatus.Success,
                Result: "# README\n..."
            ),
        },
        ExecuteError: null
    ),
    // Turn 2 input（如有）
};
```

⚠️ **`ToolCallId` 对齐是硬约束**。OpenAI converter 在 **构造请求阶段** 就严格校验 `assistant.tool_calls -> tool` 相邻关系：缺失 id 且无 `ExecuteError` 会抛 `InvalidOperationException`（**不会** 走到 HTTP，所以不是 400）。如果工具失败但你希望继续，把 `ToolResultsMessage.ExecuteError` 填上，OpenAI converter 会用 pending id 合成失败 `tool` 消息保协议合法。

### 3.2 `Tools`：声明工具签名

`ToolDefinition` + `ToolParamSpec` 是最小够用的扁平参数模型，会被 `JsonToolSchemaBuilder` 翻成 Anthropic / OpenAI 通用 JSON Schema。

```csharp
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

var readFile = new ToolDefinition(
    Name: "fs.read",
    Description: "读取文本文件全文。",
    Parameters: ImmutableArray.Create(
        new ToolParamSpec(
            name: "path",
            description: "相对仓库根的路径。",
            valueKind: ToolParamType.String,
            isNullable: false,
            example: "docs/README.md"
        ),
        new ToolParamSpec(
            name: "max_bytes",
            description: "最多读取的字节数；省略表示不限制。",
            valueKind: ToolParamType.Int32,
            isNullable: true,                                  // ← null 默认值要求 isNullable=true
            defaultValue: new ParamDefault(null)               // ← 有 default 即视为 optional
        )
    )
);

var request = new CompletionRequest(
    "Qwen3.5-27b-GPTQ-Int4",
    "You are a helpful coding agent.",
    history,
    ImmutableArray.Create(readFile)
);
```

**已知能力边界**：

- `ToolParamType` 当前只支持扁平标量：`String / Boolean / Int32 / Int64 / Float32 / Float64 / Decimal`。**没有** Object / Array——嵌套结构请扁平化或塞 JSON 字符串字段，自己在工具实现里二次解析。
- **LLM JSON 没有 `uint`**：超过 `int.MaxValue` 的整数会先解析成 `long`，自己做范围检查。
- `IsOptional` 由 **是否提供 default** 决定（`ToolParamSpec.IsOptional` 等价于 `Default.HasValue`）。
- ⚠️ 给 `defaultValue: new ParamDefault(null)` **必须** 同时设 `isNullable: true`，否则 `ToolParamSpec` 构造函数立刻抛 `ArgumentException`（`ValidateDefaultCombination`）。
- `defaultValue` 当前 **只影响** Atelia 侧的 optional 判定与 schema `required` 列表——`JsonToolSchemaBuilder` 不会把 default 值写进 JSON Schema 的 `default` 字段。**不要假设模型一定知道默认值**，关键默认行为应在 `description` 里说明。
- `ToolParamSpec.Name`：调用方应严格按声明拼写。当前 `JsonArgumentParser` 内部使用 `OrdinalIgnoreCase` 的字典做查询，所以模型大小写不一致 *暂时* 能过——但请视为实现细节，**不要依赖**。

---

## 4. 消费 `CompletionResult`

### 4.1 返回值长什么样

`StreamCompletionAsync(...)` 的返回值已经是标准聚合结果：

```csharp
var result = await client.StreamCompletionAsync(request, null, ct);
```

`CompletionResult` 承载消息体与调用元信息：

- 消息体：`Message: ActionMessage` — 结构化真相源 `Message.Blocks`
- 便捷派生视图（在 `Message` 上）：`Message.GetFlattenedText()`、`Message.ToolCalls`
- 调用元信息：`Invocation`、`Errors`

常见读取方式：

```csharp
Console.WriteLine(result.Message.GetFlattenedText()); // 所有 Text 块拼接
foreach (var call in result.Message.ToolCalls) { /* ... */ }
foreach (var err in result.Errors ?? Array.Empty<string>()) { /* ... */ }

// 回灌历史：取 Message（纯 ActionMessage，实现 IHistoryMessage）
history.Add(result.Message);
```

### 4.2 聚合契约

- 保留 provider 实际生成顺序；连续文本会合并为单个 `ActionBlock.Text`，遇到 tool_call / thinking 边界自动切块。
- 每个工具调用最终变成一个 `ActionBlock.ToolCall`，不会把 partial JSON 暴露给上层。
- 每个 thinking 块最终变成一个 `ActionBlock.Thinking`；其 `Origin` 由本次调用的 `CompletionDescriptor` 自动绑定。
- `OpaquePayload` 完全透明透传，不解析。
- 流中的 provider 错误事件被收集到 `CompletionResult.Errors`，**不抛异常**；是否中断或上报由调用方决定。
- 若流末尾一个内容块都没有，会补一个空 `ActionBlock.Text`，下游永远拿到至少一个块。

当前公共 API 不直接暴露 chunk 级增量。如果后续要做流式 UI，请在更上层另行加事件回调或单独增量接口，不要假设现有 `ICompletionClient` 仍会 `yield` chunk。

> **Agent.Core 用户**：`AgentEngine` 现在直接 await `ICompletionClient.StreamCompletionAsync(request, null, ct)`，再把返回的 `CompletionResult` 包装为 `ActionEntry`；你不需要自己再做额外聚合。
>
> 如需流式 UI 或早停，传入自定义的 `CompletionStreamObserver` 替代 `null`。详见 `CompletionStreamObserver` 的 xmldoc。

### 4.3 `ParsedToolCall` 怎么读

```csharp
public record ParsedToolCall(
    string ToolName,
    string ToolCallId,
    IReadOnlyDictionary<string, string>? RawArguments,        // 按参数名拆开的原始字符串值
    IReadOnlyDictionary<string, object?>? Arguments,          // 强类型解析后
    string? ParseError,
    string? ParseWarning
);
```

读取建议：

1. `ParseError != null` → **致命** 解析失败，`Arguments` 一定为 null（契约：`Arguments == null` ⇒ `ParseError != null`）。要么把 error 反馈给模型让它重试，要么把整条 call 标 `Skipped`。
2. `ParseError == null && Arguments != null` → 走 `Arguments[name]`，类型已按 `ToolParamSpec.ValueKind` 转换好。即使没有任何参数，`Arguments` 也是空字典而非 null。
3. `ParseWarning != null` → 部分字段降级，对应字段在 `Arguments` 里可能保留原始 string。决定是否容忍。
4. **持久化**：成功路径下，存 `RawArguments` 通常足够，重放时再走 `JsonArgumentParser` 解析；若 `ParseError` 非空，请连同 `ParseError` / `ParseWarning` 一并保存。⚠️ 当前抽象 **不保留** 完整 malformed JSON 文本——若 fatal parse 失败，原始 JSON 可能已不可恢复。需要法证级 replay 时请自行在网络层抓包。

执行完一定要回灌一条 `ToolResultsMessage`，且 `ToolResult.ToolCallId` 必须等于 `ParsedToolCall.ToolCallId`。

### 4.4 `Thinking` block

> 只做单轮 / 只要文本时可跳过本节；要把历史回灌给 thinking/reasoning 模型时必须读。

`ThinkingChunk.OpaquePayload: ReadOnlyMemory<byte>` 是 **provider-native** 序列化字节。**不要尝试解析它。** 唯一正确用法：

- 原样塞进 `ActionBlock.Thinking(origin, opaquePayload, plainTextForDebug)` 一起回灌；
- 回灌时 `Origin` 必须是 **当时产生它的那次调用** 的 `CompletionDescriptor`，否则 converter 会拒绝 replay（Anthropic 的 thinking 签名跨模型不通用，跨调用源也无意义）。

`PlainTextForDebug` 仅供日志/UI——**永远不要** 把它当成可回灌的 thinking 内容。

OpenAI Chat 路径当前 **不产出** `ActionBlock.Thinking`（`reasoning_content` 字段被丢弃）；只有 Anthropic 路径会发。如果只走 OpenAI，可以直接忽略这个 case。

### 4.5 取消与异常

- `CancellationToken` 会被传到 HTTP 流；及时取消能立即关闭连接。流式调用持有持续打开的 HTTP 连接，**不取消会一直挂着**。
- HTTP 4xx/5xx 行为按 provider 略不同：
  - **OpenAI**：抛 `HttpRequestException`，`Message` 含状态码与最多 512 字符的 response body。
  - **Anthropic**：抛 `HttpRequestException`，但 **不附带 body**（仅 `EnsureSuccessStatusCode()`）；调试时可临时在外层抓包。
- 历史构造不合法 → `OpenAIChatMessageConverter` / `AnthropicMessageConverter` 在序列化阶段就抛 `InvalidOperationException`，**不会** 走到 HTTP（最常见：tool_call_id 错位、Anthropic 首条非 user）。
- SSE 中途的 JSON 解析错误不会 throw；provider 错误事件会收集进 `CompletionResult.Errors`。

---

## 5. provider 选择速查

### 5.1 `OpenAIChatClient`

```csharp
new OpenAIChatClient(
    apiKey: null,
    httpClient: null,                              // 共享 HttpClient 时传入；不传则内部 new
    baseAddress: new Uri("http://localhost:8000/"),
    dialect: OpenAIChatDialects.SgLangCompatible,
    options: OpenAIChatClientOptions.QwenThinkingDisabled()
);
```

`options.ExtraBody` 表示把额外字段直接并到请求 JSON 根对象；它对应 SDK 语义里的 `extra_body`，不是线上的 `"extra_body"` 字段。当前本地 Qwen3.5 推荐用 `OpenAIChatClientOptions.QwenThinkingDisabled()` 注入：

```json
{
  "chat_template_kwargs": {
    "enable_thinking": false
  }
}
```

**Dialect 二选一**：

| Dialect | 适用 | 关键差异 |
|---|---|---|
| `OpenAIChatDialects.Strict` | 真·OpenAI、严格按规范的兼容端点 | 保留所有 `delta.content`（含空白） |
| `OpenAIChatDialects.SgLangCompatible` | **本地 sglang** 与一些松散兼容端点 | **仅在工具调用累积期间** 忽略夹带的纯空白 `delta.content` |

**打到本地 8000 端口通常应优先用 `SgLangCompatible`**：sglang 的工具调用流里常会夹带空白噪声，`SgLangCompatible` 会在工具调用累积期间忽略这些无意义空白。`ApiSpecId == "openai-chat-v1"`。

### 5.2 `AnthropicClient`

```csharp
new AnthropicClient(
    apiKey: null,
    httpClient: null,
    apiVersion: null,                              // 默认 "2023-06-01"
    baseAddress: new Uri("http://localhost:8000/")
);
```

走真·Anthropic 时不传 `baseAddress`（默认 `https://api.anthropic.com/`），把真 key 给 `apiKey`。`ApiSpecId == "messages-v1"`。

### 5.3 `BaseAddress` 行为（两个 client 一致）

- **总是写尾随 `/`**：URI 相对路径拼接规则下，`http://localhost:8000` 与 `http://localhost:8000/api` 这类带路径前缀的写法忘了 `/` 会拼出错误请求路径。`Program.cs` 的 `EnsureTrailingSlash()` 是稳妥模式。
- **共享 `HttpClient` 时尊重已设的 BaseAddress**：构造函数 **不会** 偷偷覆盖你已经配好的 `HttpClient.BaseAddress`，除非你显式传了 `baseAddress` 参数。
- **回退**：未显式传、且 HttpClient 也没设 → 回落各自官方端点。

### 5.4 何时关心 `ApiSpecId`

`ApiSpecId` 主要给 `CompletionDescriptor` / thinking replay / Turn lock 用。第一轮调通可以完全无视；只有当你要把 LLM 输出存档并跨进程 replay 时才需要在 descriptor 里携带它。

---

## 6. 易踩的坑 / 故障速查

### 高频坑（高密度）

1. **`Tools` 用 `default(ImmutableArray<T>)`** → converter 内部 `foreach` NRE。永远用 `ImmutableArray.Create(...)` 或 `.Empty`。
2. **拼 `role="system"` 进 `Context`** → system 走独立 `SystemPrompt` 字段。
3. **工具参数嵌套** → 当前不支持。扁平化或塞 JSON 字符串字段。
4. **`ToolCallId` 跨轮错位** → converter 抛 `InvalidOperationException`，不是 400。
5. **解析 `Thinking.OpaquePayload`** → 不要。原样回灌，`Origin` 与产生它的调用对齐。
6. **`baseAddress` 漏 `/`** → 带路径前缀时尤其会 404。
7. **本地 sglang 用 `Strict` dialect** → 工具流空白噪声会原样保留到文本块里。
8. **`null` defaultValue 但 `isNullable: false`** → `ToolParamSpec` 构造立刻抛。
9. **流式调用不传 `CancellationToken`** → HTTP 长连接一直挂着。
10. **指望模型看到 `defaultValue`** → 当前 schema 不输出 `default`，请把关键默认行为写进 `description`。

### 症状速查（本地 sglang 场景）

| 症状 | 可能原因 | 处理方向 |
|---|---|---|
| `HttpRequestException: Connection refused` | sglang 没启动 / 端口不对 | 检查服务进程与端口 |
| 404 | `baseAddress` 路径错 / 漏 `/` / 服务未暴露该 API | 核对 §5.3 |
| 400 且模型名不存在 | `ModelId` 与本地加载模型不一致 | 用 sglang 的 `/v1/models` 端点确认 |
| `InvalidOperationException` 提到 tool_call_id | 历史里 `ToolResult.ToolCallId` 与上一个 `ActionBlock.ToolCall` 错位 / 缺失 | 对齐 id；失败工具用 `ExecuteError` 让 converter 合成 |
| `InvalidOperationException` 提到 first message must be user (Anthropic) | Context 第一条不是 `ObservationMessage`（或空白被跳过后变成第一条不是 user） | 调整历史；Anthropic 协议强制首条 user |
| `ArgumentException: Default value ... isNullable` | `ToolParamSpec` 构造时 default+nullable 组合非法 | 见 §3.2 |

---

## 7. 进一步阅读

- [memory-notebook.md](./memory-notebook.md) — provider 内部实现细节、SSE 状态机、设计决策史。
- [openai-compatible-evolution.md](./openai-compatible-evolution.md) — 当你撞到新的兼容端点差异，按这份增量演进，不要堆 flag。
- `docs/Agent/Thinking-Replay-Design.md` — `ActionBlock.Thinking` / `OpaquePayload` 回灌契约全本（特别是 §3.1、§5.2）。
- `prototypes/LiveContextProto/Program.cs` — client 构造的最小活样本（看 `Main` 中的 `oaiClient = new OpenAIChatClient(...)` 几行；其余 `CharacterAgent` / `ConsoleTui` 是 prototype 私有封装，对单纯调通本库不必关心）。
- `prototypes/Completion.Abstractions/CompletionResult.cs` — `CompletionResult` record 的实现与契约。
- `prototypes/Completion/CompletionAggregator.cs` — provider 流式输出在 Completion 层内部的标准聚合器。
