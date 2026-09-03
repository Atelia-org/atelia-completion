# Atelia.Completion — 快速上手（面向使用者）

> **读者**：要在自己的代码里通过 `Atelia.Completion[.Abstractions]` 调用 LLM 的高级 LLM Agent / 上层应用作者。
> **不读这份**：要给 Completion 层加新 provider、改 SSE 解析的人——请去 [memory-notebook.md](./memory-notebook.md) 与 [openai-compatible-evolution.md](./openai-compatible-evolution.md)。
> **配套环境**：本机 `http://localhost:8888/` 上有 unsloth 服务，同时暴露 Anthropic Messages (`/v1/messages`) 与 OpenAI Chat (`/v1/chat/completions`) 两类端点。
> **最后更新**：2026-08-05

---

## 阅读路径

不同诉求挑不同章节，避免一次性吞噬所有信息：

- **只调文本**（hello-world）：§1 → §2 → §4.1 → §5 → §6
- **顺手记录 golden log**：再读 §2.1
- **直接从 golden log replay**：再读 §2.2
- **跑本地 round-trip E2E**：再读 §2.4
- **加上工具调用**：再读 §3.1、§3.2、§4.3
- **多轮回灌历史 / 用 thinking 模型**：再读 §4.2、§4.4
- **撞到本地服务报错**：直接跳 §6 故障速查

---

## 1. 心智模型

两个 csproj，对应两层关注点：

| 程序集 | 命名空间 | 你需要的核心符号 |
|---|---|---|
| `Atelia.Completion.Abstractions` | `Atelia.Completion.Abstractions` | `ICompletionClient`、`CompletionRequest`、`IHistoryMessage` 家族（含 `ActionMessage` / `ActionBlock`）、`ToolDefinition` / `ToolSchema`、`CompletionDescriptor`、`CompletionResult`、`ThinkingChunk` |
| `Atelia.Completion` | `Atelia.Completion.Anthropic`、`Atelia.Completion.OpenAI`、`Atelia.Completion.Gemini` | `AnthropicClient`、`OpenAIChatClient`、`OpenAIChatDialects`（静态访问点：`.Strict` / `.SgLangCompatible` / `.QwenSgLang` / `.DeepSeekV4`）、`GeminiClient` |

**只引 Abstractions** 用来定义请求/历史/工具——provider-neutral，不会拉出 HttpClient。
**再引 Completion** 才能拿到具体 client 实现。

一次 LLM 调用永远是这条三步曲：

```
build CompletionRequest  →  await client.StreamCompletionAsync(...)  →  CompletionResult
```

`ICompletionClient` 的基本方法负责发起流式补全，并在 client 内部完成聚合：

```csharp
Task<CompletionResult> StreamCompletionAsync(
    CompletionRequest request,
    CompletionStreamObserver? observer,
    CancellationToken cancellationToken = default
);
```

需要为单次调用表达运行策略时，使用带 `CompletionInvocationOptions` 的 overload：

```csharp
await client.StreamCompletionAsync(
    request,
    new CompletionInvocationOptions {
        PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
    },
    observer: null,
    cancellationToken: ct
);
```

`PromptCacheReuseHint` 有四档：`ConnectionDefault`、`NoReuseExpected`、
`ReuseExpectedSoon`、`ReuseExpectedAfterPause`。它是 provider-neutral、best-effort 的
**经济复用提示**，不是隐私或禁止存储保证；不支持精确控制的 provider 可以显式接受为 no-op。
这些 options 不属于逻辑 `CompletionRequest`，不进入 Prepared manifest、connection fingerprint
或 request-adapter fingerprint。只实现旧方法的第三方 client 会由默认接口实现接受
`ConnectionDefault`；其他 hint 会 fail fast，防止调用意图被静默丢弃。

`observer` 为必传参数——不需要流式观察时显式传 `null` 即可。没有单独暴露的"增量 chunk 流"公共接口。provider 仍然走流式 HTTP/SSE，但由 client 内部解析并聚合后再返回 `CompletionResult`。

---

## 2. 三十秒上手（本地 sglang，OpenAI 路径）

**复制前自检**：

- 项目引用：`Atelia.Completion.Abstractions` + `Atelia.Completion`
- 必备 using：见示例顶部
- 本地 sglang 已经监听 8000，模型名按本机加载情况替换 `ModelId`
- 本地 Qwen + sglang 使用 `OpenAIChatDialects.QwenSgLang`；其他 sglang-compatible 模型使用 `SgLangCompatible`（详见 §5.1）

```csharp
using System.Collections.Immutable;
using System.Threading;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("http://localhost:8000/")
);

var client = new OpenAIChatClient(
    apiKey: null,                                // 本地服务通常无需 key
    httpClient: httpClient,
    dialect: OpenAIChatDialects.QwenSgLang // Qwen + sglang：捕获 reasoning_content，并支持 enable_thinking
);

var request = new CompletionRequest(
    "Qwen3.5-27b-GPTQ-Int4",
    new CompletionPromptPrefix(
        "You are a helpful assistant.",
        CompletionOutputContract.ProviderDefault([]),
        [new ObservationMessage("用一句话介绍自己。")]
    ),
    tailMessages: []
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
using Atelia.Completion.Transport;

using var anthropicHttpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("http://localhost:8888/")
);

var anthropic = new AnthropicClient(
    apiKey: null,
    httpClient: anthropicHttpClient
);
```

> **跨 provider 复用 `CompletionRequest` 的边界**：纯文本 + 工具调用历史可以原样喂给任一 client，provider 差异封装在内部 converter / parser 里。**但** 含 `ActionBlock.Thinking` 的回灌历史 **不可以跨 provider 也不可以跨调用源**——`OpaquePayload` 是 provider-native 字节，`Origin` 必须是当时产出它的那次调用（详见 §4.4）。
>
> **Gemini 额外边界**：Gemini 3 的 tool continuation 依赖 `thoughtSignature`。如果上一轮 Gemini 已经产出 tool call，后续继续把 tool result 回灌给 Gemini 时，应保留该轮产出的 `GeminiReplayBlock`；只剩通用 `ToolCall` / `Text` 而丢失 replay payload 时，Gemini converter 会拒绝进行 tool replay。

**Gemini 路径** 也遵循同一套 `CompletionRequest -> StreamCompletionAsync -> CompletionResult` 形态，只是 endpoint 与 replay 约束不同：

```csharp
using Atelia.Completion.Gemini;
using Atelia.Completion.Transport;

using var geminiHttpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("https://generativelanguage.googleapis.com/")
);

var gemini = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
    httpClient: geminiHttpClient
);
```

### 2.1 用 builder 注入 golden log 文件 sink

如果你希望把每次 request / response 直接落成 golden log，推荐用高层 transport factory 创建普通 `HttpClient`，再把它注入 provider client。

当前 MVP 的 golden log 格式是 JSON Lines：每一行一个 `CompletionHttpExchange`，后续可自然演进到 replay。

```csharp
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

var transport = CompletionHttpTransportFactory.CreateFromPaths(
    new Uri("http://localhost:8000/"),
    recordLogPath: ".atelia/completion-golden/openai.jsonl",
    replayLogPath: null
);
using var httpClient = transport.HttpClient;

var client = new OpenAIChatClient(
    apiKey: null,
    httpClient: httpClient,
    dialect: OpenAIChatDialects.QwenSgLang
);

var request = new CompletionRequest(
    "Qwen3.5-27b-GPTQ-Int4",
    new CompletionPromptPrefix(
        "You are a helpful assistant.",
        CompletionOutputContract.ProviderDefault([]),
        [new ObservationMessage("用一句话介绍自己。")]
    ),
    tailMessages: []
);

var result = await client.StreamCompletionAsync(request, null, CancellationToken.None);
Console.WriteLine(result.Message.GetFlattenedText());
```

这个写法的关键点是：

- provider 仍然只看普通 `HttpClient`
- golden log capture 不会侵入 provider 代码
- JSONL file sink 只支持 Linux x64/arm64，其他平台 fail closed；`O_NOFOLLOW` 只保护最后一个 path component，因此父目录必须由 operator 控制
- JSONL 包含完整 prompt 与 provider response，应写入受控的短期诊断路径并在用后删除
- capture sink 是 best-effort 诊断旁路；sink 失败只产生不含路径、异常文本或 provider data 的 code-owned warning，不会覆盖已成功的 response、原始 transport exception 或 caller cancellation
- 因此 golden log 可能缺失，不能当作权威持久化、审计记录或远程调用成功回执
- 后续想切到 replay，只需要替换 transport factory 的装配方式

### 2.2 用 builder 从 JSONL golden log 直接 replay

当你已经有一份 JSONL golden log 时，可以把真实网络替换成顺序 replay：

```csharp
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(
    new Uri("http://localhost:8000/"),
    ".atelia/completion-golden/openai.jsonl"
);

var client = new OpenAIChatClient(
    apiKey: null,
    httpClient: httpClient,
    dialect: OpenAIChatDialects.QwenSgLang
);
```

当前 replay 语义是：

- 严格顺序消耗 golden log 中的下一条 exchange
- 校验 `method`、`requestUri`、`requestText`
- 校验失败会立即抛错，提醒请求已经偏离录制时的行为

这更适合 deterministic 调试和单测，不是通用匹配型 mock server。

### 2.3 LiveContextProto 的本地开关

`prototypes/LiveContextProto/Program.cs` 已经接好 transport factory，可以直接靠环境变量切换模式：

- `ATELIA_COMPLETION_GOLDEN_LOG=/path/to/openai.jsonl`：真实访问远程服务，同时记录 golden log
- `ATELIA_COMPLETION_REPLAY_LOG=/path/to/openai.jsonl`：不访问远程服务，直接从 golden log 顺序 replay

程序启动时会打印当前 transport 模式，例如：

```text
[startup] transport: record -> .atelia/completion-golden/openai.jsonl (http://localhost:8000/)
[startup] env: ATELIA_COMPLETION_GOLDEN_LOG=record, ATELIA_COMPLETION_REPLAY_LOG=replay
```

两个环境变量不能同时设置。

### 2.4 本地 round-trip E2E 测试怎么跑

`CompletionHttpTransportTests` 里已经有两条显式启用的本地 E2E：

- `LocalRoundTripE2E_OpenAI_RecordThenReplayAgainstLocalEndpoint`
- `LocalRoundTripE2E_Anthropic_RecordThenReplayAgainstLocalEndpoint`

它们都会对真实的 `http://localhost:8000/` 做完整闭环：

1. 先走 live request
2. 把 exchange 记录到 JSONL golden log
3. 再从同一份 JSONL 做 replay
4. 验证 replay 结果与 live 结果一致

这两条测试默认**不会真正执行**，只有显式设置环境变量才会跑：

```bash
export ATELIA_RUN_LOCAL_LLM_E2E=1
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj --filter "LocalRoundTripE2E"
```

如果你只想跑其中一条，也可以用更细的过滤条件：

```bash
export ATELIA_RUN_LOCAL_LLM_E2E=1
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj --filter "LocalRoundTripE2E_OpenAI"

export ATELIA_RUN_LOCAL_LLM_E2E=1
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj --filter "LocalRoundTripE2E_Anthropic"
```

使用前提：

- 本地 `sglang` 服务已经监听 `http://localhost:8000/`
- 该端点同时支持 OpenAI Chat 与 Anthropic Messages
- Anthropic surface 还需支持 `GET /v1/models/{modelId}` 并返回正整数 `max_tokens`；没有静态 fallback
- 本地服务不校验 `model-id` 与 `api-key`，因此测试中的占位值不会影响运行

推荐把它视为**显式运行的本地集成测试**，而不是默认快速单测：

- 默认 `dotnet test` 不会被本地服务状态影响
- 需要验证 transport 的真实闭环时，再手动开启
- 这是比“只测录制”或“只测回放”更强的一条验证路径

---

## 3. 构造 `CompletionRequest`

```csharp
public sealed class CompletionPromptPrefix {
    public string SystemPrompt { get; }                            // 空字符串可以，不要传 null
    public CompletionOutputContract OutputContract { get; }       // ordered tools + tool-call policy
    public ImmutableArray<IHistoryMessage> SharedContextMessages { get; }
}

public sealed class CompletionRequest {
    public string ModelId { get; }                                 // 必填，且必须能被服务端识别
    public CompletionPromptPrefix PromptPrefix { get; }            // typed stable-prefix boundary
    public ImmutableArray<IHistoryMessage> TailMessages { get; }   // 不共享的请求尾部
}
```

这些对象在构造时冻结消息和工具集合；要“修改”一次请求，应显式新建对象。没有旧的 flat
`SystemPrompt` / `Context` / `Tools` compatibility view。

`CompletionOutputContract` 是 ordered tools、`CompletionToolChoice` 和可选
`AllowParallelToolCalls` 的单一真相源。`CompletionToolChoice` 支持
`ProviderDefault` / `Auto` / `None` / `RequiredAny` / `RequiredNamed(name)`；provider
无法等价表达某个策略时会 fail fast。

普通、不需要共享前缀的调用可把全部旧历史放入 `SharedContextMessages`，并令
`TailMessages = []`。需要共享 prefix 的调用必须显式划分两段，不能用裸 message index 或
adapter 内字符串搜索表达 boundary。

**有意不支持 output-token cap**：`CompletionRequest` 与 strict connections V2 都不提供
`MaxTokens` / `MaxOutputTokens` 一类数字。省略字段即可表达不限量/模型最大值的 provider wire 一律省略；必须
显式发送数字才能得到该语义时，client 先查询 exact `ModelId` 的官方 model capability，并且只发送 provider
报告的模型最大值。这里不会再引入
per-request、per-connection 或换名后的本地输出预算，避免把已经计费的生成截断成不可用结果。V1
connections manifest 会被拒绝，V2 中残留的 `maxTokens`（包括 `null`）也会作为 unknown property 被拒绝。

**其他当前不暴露的字段**（按需走 provider 默认值，未来再扩展）：`temperature`、`top_p`、`stop`、`response_format` 等。

⚠️ **不要把 system 拼进 messages**——它走 `PromptPrefix.SystemPrompt`，与历史解耦。

⚠️ **Tools 只从 `PromptPrefix.OutputContract.Tools` 进入 provider wire**。不要在 member/tail
另建一份工具集合或 tool-choice override。

### 3.1 Shared context / tail：消息仍使用 RL 术语

`IHistoryMessage` 故意 **不叫** `user/assistant/system/tool`——它向强化学习靠拢，为多 Agent 场景预留。映射规则：

| 你想表达 | 用什么 | 备注 |
|---|---|---|
| 用户输入 / 系统通知 / 环境观测 | `new ObservationMessage(string? content)` | 统一文本字段 |
| LLM 上一次输出（要回灌） | `CompletionResult.Message`（取聚合结果的 `ActionMessage` 字段，详见 §4.2） | 纯 `ActionMessage` 实现 `IHistoryMessage`，可放入 shared context 或 tail |
| 工具执行结果（要回灌） | `new ToolResultsMessage(content, results)` | `results: IReadOnlyList<ToolResult>`，且必须与 pending tool call 按 `ToolCallId + ToolName` 一一对齐 |

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
        content: null,
        results: new[] {
            ToolResult.FromText(
                toolName: "fs.read",
                toolCallId: "call_abc123",                          // 必须与上一步 ActionBlock.ToolCall 的 Id 对齐
                status: ToolExecutionStatus.Success,
                content: "# README\n..."
            ),
        }
    ),
    // Turn 2 input（如有）
};
```

⚠️ **`ToolCallId + ToolName` 对齐是硬约束**。三个 provider converter 在 **构造请求阶段** 都会严格校验 pending tool call 与 `ToolResultsMessage.Results` 的 1:1 对齐关系：缺失、重复、错位，或 `ToolCallId` 对上但 `ToolName` 不一致，都会直接抛 `InvalidOperationException`（**不会** 走到 HTTP，所以不是 400）。如果工具失败但你希望继续，请照常回灌该工具自己的 `ToolResult`，并把失败语义放进 `ToolResult.Status` 与 `ToolResult.Blocks`，而不是依赖旁路字段。

### 3.2 `Tools`：声明工具签名

`ToolDefinition` 现在直接以 `InputSchema` 作为唯一 schema 真源。推荐显式写 `ToolSchema.Object / Array / Value`，不要再按旧 flat API 去想工具签名。

```csharp
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

var readFile = new ToolDefinition(
    name: "fs.read",
    description: "读取文本文件全文。",
    inputSchema: new ToolSchema.Object(
        properties: [
            new ToolSchema.Property(
                name: "path",
                schema: new ToolSchema.Value(
                    ToolParamType.String,
                    description: "相对仓库根的路径。",
                    example: "docs/README.md"
                ),
                isRequired: true
            ),
            new ToolSchema.Property(
                name: "max_bytes",
                schema: new ToolSchema.Value(
                    ToolParamType.Int32,
                    description: "最多读取的字节数；省略表示不限制。"
                ),
                isRequired: false
            ),
        ]
    )
);

var request = new CompletionRequest(
    "Qwen3.5-27b-GPTQ-Int4",
    new CompletionPromptPrefix(
        "You are a helpful coding agent.",
        CompletionOutputContract.ProviderDefault([readFile]),
        history
    ),
    tailMessages: []
);
```

**已知能力边界**：

- `ToolDefinition` / `ITool` 当前主线已经收口到 `Definition -> InputSchema -> ToolSchema`；工具声明请直接围绕 `ToolSchema` 思考。
- `ToolDefinition.InputSchema` 根节点必须是 `ToolSchema.Object`；其内部可递归声明 `object / array / value`。
- `ToolSchema.Property.IsRequired` 负责表达字段是否必填；不要再把 `defaultValue` 当成 optional 语义来源。
- `ToolSchema.Value` 的标量 kind 仍由 `ToolParamType` 表达：`String / Boolean / Int32 / Int64 / Float32 / Float64 / Decimal`。
- `ToolSchema.Object.AdditionalProperties` 默认是 `false`；模型传入未知字段时，执行边界会把它当 parse error，而不是 warning 后透传。
- 字段名按 `Ordinal` 精确匹配；大小写不一致会被当成不同字段，不要依赖“大小写差一点也能过”。
- 若你只想从 `record class` / `class` 直接生成 `ToolDefinition`，可用 `Atelia.Completion.Tools.Declaration.ReflectedToolDefinitionBuilder.BuildDefinitionUsingTypeDescription<TInput>(toolName)`；若你要拿到可执行工具，则优先用 `MethodToolWrapper.FromMethod/FromDelegate` 或 `ArtifactToolWrapper<T>.Create(...)`。
- **LLM JSON 没有 `uint`**：超过 `int.MaxValue` 的整数会先解析成 `long`，自己做范围检查。
- 若你给 `ToolSchema.Value(defaultValue: new ParamDefault(null))`，仍然必须同时设 `isNullable: true`，否则构造函数会抛 `ArgumentException`。
- `defaultValue` 当前不会被 `JsonToolSchemaBuilder` 写进 JSON Schema 的 `default` 字段。关键默认行为请继续写进 `description`。
- 执行侧主链现在是“共享 `ToolRegistry` 持有工具能力；每个 `ToolSessionState` 通过 `ToolAccessPolicy` 投影可见工具；`ToolExecutor` 作为 session 级执行壳负责分发 `RawToolCall`”。像 `MethodToolWrapper` 这类 schema-bound tool 会在内部解析 `RawArgumentsJson`；其 tool-level description 来自 `[Tool]`，字段 description 来自输入 DTO 属性上的 `[Description]` 等声明，再通过 `ToolExecutionContext` 读取 session/items/services。

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

### 4.3 `RawToolCall` 怎么读

```csharp
public record RawToolCall(
    string ToolName,
    string ToolCallId,
    string RawArgumentsJson                                  // provider 原始 arguments JSON 文本
);
```

读取建议：

1. `RawArgumentsJson` 是 **provider 发来的完整 arguments 文本**。Completion 抽象层不再替你做 schema-aware 解析。
2. 需要执行工具时，先用共享 `ToolRegistry` + 当前 `ToolSessionState` 创建 `Atelia.Completion.Tools.ToolExecutor`，再把 `RawToolCall` 交给它；它会按同一份 `ToolAccessPolicy` 做执行授权并分发给具体 `ITool`。若该工具需要 schema 绑定，例如 `MethodToolWrapper`，会在自己的 `ExecuteAsync(ToolExecutionContext, ...)` 内部按当前 `ToolDefinition.InputSchema` 解析 `RawArgumentsJson`。
3. **持久化 / replay**：直接保存 `RawArgumentsJson`。回放给 OpenAI Chat 时原样作为 `function.arguments`；回放给 Anthropic/Gemini 这类结构化参数协议时，再把这段 JSON parse 成对象。
4. **法证价值**：相比旧设计，当前抽象会保留完整原始 JSON 文本；即使 arguments malformed，也不会因为 provider 预解析失败而丢证据。

补充边界：

- `ParseError` / `ParseWarning` 不再是 `ToolExecutor` 的统一产物；它们是否存在、以什么形式体现，取决于具体 tool 的内部绑定策略。
- `ArtifactToolWrapper<T>` 已落地为现行能力：它同样从 `ToolExecutionContext.RawToolCall` 读取原始参数，内部先做 schema 校验，再反序列化为声明式类型，并补做 model validation / handler validation。

执行完一定要回灌一条 `ToolResultsMessage`，且 `ToolResult.ToolCallId` 必须等于 `RawToolCall.ToolCallId`。

### 4.4 `ReasoningBlock` / provider-native replay block

> 只做单轮 / 只要文本时可跳过本节；要把历史回灌给 thinking/reasoning 模型时必须读。

provider-specific `ActionBlock.ReasoningBlock`（例如 `AnthropicReasoningBlock`、`OpenAIChatReasoningBlock`、`GeminiReplayBlock`）承载 **provider-native** replay 信息。对这类 block 的共同规则是：

- 不要手工解析后再重组为“通用 thinking 文本”；优先原样保留 provider-native payload；
- 回灌时 `Origin` 必须是 **当时产生它的那次调用** 的 `CompletionDescriptor`，否则 converter 会拒绝 replay（Anthropic 的 thinking 签名、Gemini 的 `thoughtSignature` 都与调用源绑定）；
- 如果你修改了同一条 `ActionMessage` 里的可见 `Text` / `ToolCall`，也必须同步更新对应 provider replay block；Gemini 路径会对这两份信息做一致性校验，避免出现双真源漂移。

`PlainText` 是统一的明文视图，供 UI 展示 / 日志 / 审计使用——**永远不要** 把它当成可回灌的 provider-native authority。provider 只给出加密负载时（如 Anthropic 的 `redacted_thinking`）它为 `null`；同时存在明文视图与原生 payload 时，provider codec 会校验两者一致。

只依赖 `Completion.Abstractions` 的 offline reader 可能没有加载产生该块的 provider codec。此时 registry 会解码为 `ActionBlock.OpaqueReasoningBlock`：它精确保留 codec id、`Origin`、provider-native payload 与可选 `PlainText`，保证 deserialize → serialize 不改变持久化 authority，而不会再把 debug 文本降级成新的 replay 内容。这是 semantic hard cut；旧版 unknown-codec → `TextReasoningBlock` 的 lossy 解释不再保留。这个 opaque carrier 不授予回灌能力；执行真实 provider 调用的进程仍须注册对应 codec，使相同 payload 解码为 provider-specific block。

OpenAI Chat 路径的 `Strict` / `QwenSgLang` 会把 `reasoning_content` 捕获为 `OpenAIChatReasoningBlock`，但不把它当作可回灌 authority；通用 `SgLangCompatible` 忽略该扩展字段。`OpenAIChatDialects.DeepSeekV4` 与 `DeepSeekV4ChatClient` 则会同时捕获并在下一轮 assistant 历史中回灌 `reasoning_content`，以维持 DeepSeek V4 tool calling continuity。

Gemini 路径的特殊点是：

- `thoughtSignature` 附着在 text / functionCall part 上，而不是独立 thinking 事件里；
- 因此 Gemini 使用 `GeminiReplayBlock` 保存整轮 provider-native `parts[]` 快照；
- tool continuation 时应优先保留这份 replay block。

### 4.5 取消与异常

- runtime不设置elapsed-operation timeout或stream-idle timeout。不可见reasoning、排队或长时间无SSE frame
  都不会被推断为LLM failure；调用会耐心等待，直到provider terminal、明确transport/protocol错误或caller cancellation。
- `CancellationToken`原样传入HTTP流；caller cancellation会关闭连接并保留原token identity。
- 四个provider共享HTTP status与成功响应`text/event-stream`校验。HTTP 4xx/5xx抛`HttpRequestException`，
  message包含状态码与有界response body；建连/读取错误保持原transport exception。
- 历史构造不合法 → `OpenAIChatMessageConverter` / `AnthropicMessageConverter` 在序列化阶段就抛 `InvalidOperationException`，**不会** 走到 HTTP（最常见：tool_call_id 错位、Anthropic 首条非 user）。
- SSE malformed JSON、known event shape错误和event/data type冲突会fail closed；provider明确的失败事件形成
  `Failed` termination。terminal evidence前EOF抛`CompletionStreamInterruptedException`，表示远端结果不确定，
  不会伪装成普通`Incomplete`或自动重试。Anthropic的唯一窄兼容是：blocks全关、已收到非空
  `message_delta.stop_reason`后的无pending-frame clean EOF可按该reason结束；read exception、取消、pending frame
  和active block均不适用。完整矩阵见
  [`src/Completion/README.md`](../../src/Completion/README.md)。
- OpenAI Responses只按typed `response.refusal.delta/done`、message refusal content或最终response output fallback
  识别模型拒答，不从普通文本猜测。refusal正文仍可作为transient text/observer delta显示，但必须等最终
  `response.completed` / `response.incomplete` 后才收口为`Incomplete(response.refusal)`；正文不进入errors或termination
  metadata。同一时刻只允许一个未finalized refusal content；已知message/output容器shape malformed或content交错会
  fail closed，合法unknown string type仍forward-compatible。refusal done不是terminal，terminal前EOF仍是outcome
  uncertain；`response.failed` / `error`仍优先为Failed。

---

## 5. provider 选择速查

### 5.1 `OpenAIChatClient`

```csharp
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("http://localhost:8000/")
);

new OpenAIChatClient(
    apiKey: null,
    httpClient: httpClient,
    dialect: OpenAIChatDialects.QwenSgLang,
    options: new OpenAIChatClientOptions {
        ReasoningEffort = CompletionReasoningEffort.Disabled
    }
);
```

`CompletionReasoningEffort` 提供 `ProviderDefault`、`Disabled`、`Low`、`Medium`、`High`、`Max` 六档。`ProviderDefault` 不发送显式控制；其余档位由所选 provider surface 映射为厂商 wire 值。Qwen surface 将 `Disabled` 映射为：

```json
{
  "chat_template_kwargs": {
    "enable_thinking": false
  }
}
```

**Dialect 选择**：

如果目标就是 DeepSeek V4，通常不需要自己选 dialect，直接用 `DeepSeekV4ChatClient` 更省心：

```csharp
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("https://api.deepseek.com/")
);

new DeepSeekV4ChatClient(
    apiKey: Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"),
    httpClient: httpClient
);
```

通用 `OpenAIChatClient` 仍适合需要手动控制端点和 dialect 的场景。

| Dialect | 适用 | 关键差异 |
|---|---|---|
| `OpenAIChatDialects.Strict` | 真·OpenAI、严格按规范的兼容端点 | 保留所有 `delta.content`（含空白） |
| `OpenAIChatDialects.SgLangCompatible` | **本地 sglang** 与一些松散兼容端点 | **仅在工具调用累积期间** 忽略夹带的纯空白 `delta.content` |
| `OpenAIChatDialects.QwenSgLang` | **Qwen + sglang** | 继承 sglang 空白处理；捕获明文 `reasoning_content`；将统一档位映射为 `chat_template_kwargs.enable_thinking` |
| `OpenAIChatDialects.DeepSeekV4` | **DeepSeek V4** 等要求 assistant tool replay 保留 `reasoning_content` 的端点 | 捕获 `delta.reasoning_content` 为 `OpenAIChatReasoningBlock`，并在回灌 assistant 历史时写回 `reasoning_content` |

本地 Qwen 应优先用 `QwenSgLang`；只有无法确认模型族、或端点不支持 Qwen `enable_thinking` 扩展时才用通用 `SgLangCompatible`。后者没有统一 reasoning 档位映射，若配置非 `ProviderDefault` 会 fail fast。`ApiSpecId == "openai-chat-v1"`。

### 5.2 `AnthropicClient`

```csharp
using Atelia.Completion.Anthropic;
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("http://localhost:8000/")
);

new AnthropicClient(
    apiKey: null,
    httpClient: httpClient,
    apiVersion: null, // 默认 "2023-06-01"
    reasoningEffort: CompletionReasoningEffort.High,
    promptCacheTtl: AnthropicPromptCacheTtl.OneHour
);
```

走真·Anthropic 时，用 `CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://api.anthropic.com/"))` 创建 `HttpClient`，再把真 key 给 `apiKey`。`ApiSpecId == "messages-v1"`。

Anthropic Messages 的 `max_tokens` 是必填字段。client 在首次使用某个 exact `ModelId` 时先调用
`GET /v1/models/{modelId}`，严格读取 `ModelInfo.max_tokens`，成功后才发送 Messages POST；成功值按 client
lifetime/exact id 缓存，并发查询 one-flight，失败或取消不会污染缓存。没有静态 fallback，也没有调用方数字配置。

**Adaptive thinking**：`ProviderDefault` 不发送显式控制，`Disabled` 发送 `thinking.type=disabled`，`Low`～`Max` 发送 `thinking.type=adaptive`、`display=summarized` 与对应的 `output_config.effort`。开启后：

- 明文 thinking 通过 `CompletionStreamObserver.ReceivedReasoningDelta` 流式推送，并在 `AnthropicReasoningBlock.PlainText` 上留存完整快照；
- 被安全系统加密的 `redacted_thinking` 块仍会产出 `AnthropicReasoningBlock`（`PlainText == null`），以便原样回灌。

配置驱动的调用方在 `CompletionConnectionConfig.ReasoningEffort`（JSON: `reasoningEffort`）上使用同一组稳定字符串档位。该值会进入 connection fingerprint；provider 映射版本会进入 request-adapter fingerprint，恢复时不会静默沿用不同 reasoning 语义。

当前映射摘要：OpenAI Chat / Responses 使用 `none|low|medium|high|xhigh`；OpenAI Responses 在启用档位下同时请求 `summary=auto`；Qwen 映射为 `enable_thinking` 布尔开关；DeepSeek V4 的 `Disabled` 映射为 `thinking.type=disabled`，启用档位显式发送 `thinking.type=enabled`，并将 `Low|Medium|High` 收敛到 `reasoning_effort=high`、`Max` 映射为 `max`；Anthropic 使用 adaptive thinking 与同名 effort。

**Anthropic prompt cache TTL**：`AnthropicPromptCacheTtl` 是 provider-specific 配置，稳定 JSON 字符串为 `provider-default|5m|1h`。`provider-default`（默认）保持原有 wire，不发送 `cache_control.ttl`；`5m` 与 `1h` 会把相应 `ttl` 写到当前的 system、tool definitions 和最新 user/assistant message 三处 cache breakpoint。例如：

```json
{
  "kind": "anthropic",
  "anthropicPromptCacheTtl": "1h"
}
```

非 Anthropic connection 使用非默认值会在配置加载或 client factory 创建时 fail fast。TTL 是可调整的运行策略：它进入 `atelia.completion.call-log.v10` 的 connection snapshot，方便审计 connection 默认值，但不进入 durable connection/request-adapter fingerprint，因此只改变 TTL 不会令已准备请求失去恢复身份。v10 从 connection/request snapshot 删除了 output-cap 字段，并继续显式记录 object schema 的 nullable 语义。`CompletionOutputContract.SemanticFingerprint`采用条件式wire版本：递归schema中没有nullable Object时继续使用原`atelia.completion.output-contract.v1` preimage，保持既有fingerprint；首次出现`ToolSchema.Object.IsNullable=true`时才使用显式提交每个Object nullability的v2 preimage。

单次调用的 `PromptCacheReuseHint` 优先级如下：当 `enablePromptCaching=false` 时始终不发送 cache breakpoint；否则 `ConnectionDefault` 沿用 connection 的 `AnthropicPromptCacheTtl`，`NoReuseExpected` 不发送 `cache_control`，`ReuseExpectedSoon` 映射到 `5m`，`ReuseExpectedAfterPause` 映射到 `1h`。OpenAI Chat、OpenAI Responses、Gemini 与 DeepSeek 当前接受这些 hint，但只提供 implicit/best-effort 行为，不伪装成显式 breakpoint 保证；Gemini explicit CachedContent 属于独立 resource lifecycle，不在单次 invocation options 中映射。

Call log v10 在 `request.promptPrefix` / `request.tailMessages` 中保留 typed boundary，完整记录
`OutputContract` 的 ordered tools、tool choice、parallel policy 与 canonical semantic fingerprint。
`response.usage` 分别记录 uncached input、cache creation、cache read 与 output tokens；
`promptCache.requestStatus / supportStatus / observationStatus` 是相互独立的状态。
`Complete` 且 read/write 都为零只表示 `noCacheIoObserved`，不能推断 provider miss 原因。
OpenAI strict Chat surface 会发送 `stream_options.include_usage=true` 并读取 `[DONE]` 前的最终usage
snapshot；兼容方言默认不添加该字段。Responses读取terminal response usage，Gemini接受任意chunk中的
`usageMetadata`并保留逐字段最后一个非空累计值。
这些 usage、`context.sourceId`、family/call-role attribution 都只用于观测，不参与 durable request、
Maintainer capability 或 recovery identity。

### 5.3 `GeminiClient`

```csharp
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("https://generativelanguage.googleapis.com/")
);

new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
    httpClient: httpClient
);
```

Gemini 的 `maxOutputTokens` 在省略时会采用随模型变化的默认值，因此 client 在首次使用某个 exact
`ModelId` 时先调用 `GET /v1beta/models/{modelId}`，严格读取 `Model.outputTokenLimit`，再把该最大值写入
`generationConfig.maxOutputTokens`。成功值按 client lifetime/exact id 缓存，并发查询 one-flight；失败或取消不
poison cache，且 capability 成功前不会发送 billable generation POST。

当前 Gemini 实现走 Google AI Studio / Gemini Developer API：

- 非流式底层接口：`models.generateContent`
- 流式底层接口：`models.streamGenerateContent?alt=sse`
- `PromptPrefix.SystemPrompt` 会投影到 `systemInstruction`
- `ToolDefinition[]` 会投影到 `tools[].functionDeclarations[]`

Gemini 的特殊点不是构造方式，而是历史回灌：

- 纯文本多轮：可以像其他 provider 一样直接回灌 `CompletionResult.Message`
- tool continuation：必须保留同轮产出的 `GeminiReplayBlock`，因为 `thoughtSignature` 是 Gemini 验证 function-call continuity 的一部分
- 如果 `GeminiReplayBlock` 与同一条消息里的可见 `Text` / `ToolCall` 不一致，Gemini converter 会直接失败，而不是静默选择其中一份
- 因此最稳妥的做法是直接回灌原始 `CompletionResult.Message`，不要手工裁剪 Gemini block 再继续 tool continuation

### 5.4 `BaseAddress` 行为（几个 client 一致）

- **推荐走 `CompletionHttpTransportFactory.CreateLiveClient(...)`**：factory 会把 base address 规范化为适合相对路径拼接的形式；`http://host/prefix` 会自动收敛成 `http://host/prefix/`。
- **provider 只校验，不改写**：`OpenAIChatClient` / `AnthropicClient` / `GeminiClient` 要求 `HttpClient.BaseAddress` 已配置、为 absolute URI、且以 `/` 结尾；若不满足会立刻抛错。
- **不要把 query / fragment 放进 BaseAddress**：BaseAddress 只承载稳定 endpoint 前缀；查询参数和 provider 行为开关应走请求参数或 options。

### 5.5 何时关心 `ApiSpecId`

`ApiSpecId` 主要给 `CompletionDescriptor` / thinking replay / Turn lock 用。第一轮调通可以完全无视；只有当你要把 LLM 输出存档并跨进程 replay 时才需要在 descriptor 里携带它。

---

## 6. 易踩的坑 / 故障速查

### 高频坑（高密度）

1. **在 prefix 外另传 tools / tool-choice** → tools 与调用策略只走 `CompletionOutputContract`。
2. **拼 `role="system"` 进 messages** → system 走独立的 `PromptPrefix.SystemPrompt`。
3. **把嵌套 schema 当成旧 flat 参数来读** → `ToolSchema` 已支持嵌套 object / array；执行结果会物化为 dictionary/list，不会自动绑成你的自定义 CLR 类型。
4. **`ToolCallId` 跨轮错位** → converter 抛 `InvalidOperationException`，不是 400。
5. **解析 `Thinking.OpaquePayload`** → 不要。原样回灌，`Origin` 与产生它的调用对齐。
6. **手写 `HttpClient.BaseAddress` 时漏 `/`** → 带路径前缀时尤其会 404；优先走 `CompletionHttpTransportFactory.CreateLiveClient(...)`。
7. **本地 sglang 用 `Strict` dialect** → 工具流空白噪声会原样保留到文本块里。
8. **`ToolSchema.Value(defaultValue: new ParamDefault(null))` 但 `isNullable: false`** → 构造立刻抛。
9. **流式调用不传 `CancellationToken`** → HTTP 长连接一直挂着。
10. **指望模型看到 `defaultValue`** → 当前 schema 不输出 `default`，请把关键默认行为写进 `description`。

### 症状速查（本地 sglang 场景）

| 症状 | 可能原因 | 处理方向 |
|---|---|---|
| `HttpRequestException: Connection refused` | sglang 没启动 / 端口不对 | 检查服务进程与端口 |
| 404 | `HttpClient.BaseAddress` 路径错 / 手写时漏 `/` / 服务未暴露该 API | 核对 §5.3 |
| 400 且模型名不存在 | `ModelId` 与本地加载模型不一致 | 用 sglang 的 `/v1/models` 端点确认 |
| `InvalidOperationException` 提到 tool_call_id | 历史里 `ToolResult.ToolCallId` 与上一个 `ActionBlock.ToolCall` 错位 / 缺失 / 数量不一致 | 对齐 id，并为每个 pending tool call 都回灌一条 `ToolResult` |
| `InvalidOperationException` 提到 first message must be user (Anthropic) | shared context / tail 的第一条不是 `ObservationMessage`（或空白被跳过后变成第一条不是 user） | 调整历史；Anthropic 协议强制首条 user |
| `ArgumentException: Default value ... isNullable` | `ToolSchema.Value` 构造时 default+nullable 组合非法 | 见 §3.2 |

---

## 7. 进一步阅读

- [memory-notebook.md](./memory-notebook.md) — provider 内部实现细节、SSE 状态机、设计决策史。
- [openai-compatible-evolution.md](./openai-compatible-evolution.md) — 当你撞到新的兼容端点差异，按这份增量演进，不要堆 flag。
- `docs/Agent/Thinking-Replay-Design.md` — `ActionBlock.Thinking` / `OpaquePayload` 回灌契约全本（特别是 §3.1、§5.2）。
- `prototypes/LiveContextProto/Program.cs` — client 构造的最小活样本（看 `Main` 中的 `oaiClient = new OpenAIChatClient(...)` 几行；其余 `CharacterAgent` / `ConsoleTui` 是 prototype 私有封装，对单纯调通本库不必关心）。
- `src/Completion.Abstractions/CompletionResult.cs` — `CompletionResult` record 的实现与契约。
- `src/Completion/CompletionAggregator.cs` — provider 流式输出在 Completion 层内部的标准聚合器。
