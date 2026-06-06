# Atelia.Completion.Tools - 快速上手（面向使用者）

> **读者**：要把宿主能力或结构化产物暴露给 LLM tool calling 的上层应用作者。
> **不读这份**：要修改 schema 反射、raw JSON 绑定或执行器内部实现的人。那类工作请直接看 `Declaration/ReflectedToolDefinitionBuilder.cs`、`ObjectInputToolRuntime.cs` 和对应测试。
> **配套阅读**：`Atelia.Completion` 的 client / `CompletionRequest` 用法见 [../docs/Completion/quick-start.md](../../docs/Completion/quick-start.md)。本 README 只覆盖 tool 的定义、注册、执行和回灌。

---

## 1. 这层库解决什么问题

如果你直接把宿主能力暴露给 LLM，通常会很快遇到三类重复劳动：

- 手写 `ToolDefinition` / JSON schema。
- 手写 `RawArgumentsJson` 的解析、反序列化、DataAnnotations 校验。
- 手写“本轮对模型可见哪些工具、执行后怎样桥接回 `ToolResultsMessage`”的样板代码。

`Atelia.Completion.Tools` 把这三件事收口成一条统一主链：

```text
DTO / Artifact class
    ↓ 反射
MethodToolWrapper / ArtifactToolWrapper<T>
    ↓ 注册
ToolRegistry
    ↓ session 投影
ToolSessionState
    ↓ 暴露给模型 + 执行 RawToolCall
ToolExecutor
    ↓
CompletionRequest.Tools / ToolResultsMessage
```

它提供三种核心能力：

1. **`MethodToolWrapper`**：把一个正常的业务方法包装成 `ITool`。适合“读文件”“查索引”“调用宿主服务”这类能力型工具。
2. **`ArtifactToolWrapper<T>`**：把“让模型交一个结构化产物 `T`”包装成工具。适合 outline、patch plan、抽取结果、排序结果这类产物型工具。
3. **`ToolExecutor`**：按 session 可见性投影工具定义，并把 `RawToolCall` 执行成 `ToolResult`，用于接 `CompletionRequest` 和 `ToolResultsMessage`。

---

## 2. 心智模型

先分清三层状态：

- **`ToolRegistry`**：共享注册表。通常跟宿主或 app 生命周期一致。
- **`ToolSessionState`**：单个 LLM session 的运行态。携带可见性策略、`IServiceProvider`、任意 `Items`。
- **`ToolExecutor`**：绑定某个 `registry + session` 的轻量执行壳。

`ToolExecutionContext` 是工具真正看到的运行时入口，里面有：

- `Session`：当前 `ToolSessionState`
- `RawToolCall`：模型原始发出的工具调用
- `ExecutionSequence`：本 session 内单调递增的执行序号
- `Services` / `Items`：直接透出自 `ToolSessionState`

这意味着：

- 工具 schema 是注册时确定的。
- 工具可见性是 session 级决定的。
- 工具执行时的上下文数据也应该从 session 注入，而不是散落在全局静态变量里。

---

## 3. 先跑通：`MethodToolWrapper`

`MethodToolWrapper` 适合“宿主能力型工具”。主路径只有四步：

1. 写一个输入 DTO。
2. 写一个标了 `[Tool]` 的方法。
3. 用 `MethodToolWrapper.FromMethod(...)` 或 `FromDelegate(...)` 包装。
4. 注册到 `ToolRegistry`，再由 `ToolExecutor` 执行。

### 3.1 最小示例

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

public sealed class EchoTools {
    [Tool("workspace.echo", "Echo text and expose session scope.")]
    public ValueTask<ToolExecuteResult> EchoAsync(
        EchoInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken
    ) {
        _ = cancellationToken;

        var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
            ? value as string
            : null;

        return ValueTask.FromResult(
            ToolExecuteResult.FromText(
                ToolExecutionStatus.Success,
                $"{input.Text}|{scope}|{context.ExecutionSequence}"
            )
        );
    }
}

[Description("Input for workspace.echo.")]
public sealed record class EchoInput(
    [property: Description("Text to echo back.")]
    [property: JsonPropertyName("text")]
    string Text
);

var host = new EchoTools();
var echoTool = MethodToolWrapper.FromMethod(
    host,
    typeof(EchoTools).GetMethod(nameof(EchoTools.EchoAsync))!
);

var registry = new ToolRegistry([echoTool]);
var session = new ToolSessionState(
    items: new Dictionary<string, object?> { ["scope"] = "quick-start" }
);
var executor = new ToolExecutor(registry, session);

var execution = await executor.ExecuteAsync(
    new RawToolCall("workspace.echo", "call-1", """{"text":"hello"}"""),
    CancellationToken.None
);

Console.WriteLine(execution.ExecuteResult.GetFlattenedText());
// hello|quick-start|1
```

### 3.2 这个例子里真正发生了什么

- `[Tool(...)]` 提供 **tool-level description**。它不是从 DTO 的 `[Description]` 推出来的。
- 输入 DTO 的字段说明来自 DTO 属性上的 `[Description]` / `[JsonPropertyName]` / DataAnnotations。
- 模型只会看到第一个业务输入参数，也就是 `EchoInput` 的 schema。
- `ToolExecutionContext` 和 `CancellationToken` 是运行时基础设施参数，**不会暴露给模型**。

`MethodToolWrapper` 目前强制要求方法签名是：

```csharp
ValueTask<ToolExecuteResult> Method(
    TInput input,
    ToolExecutionContext context,
    CancellationToken cancellationToken
)
```

并且该方法必须带 `[Tool(name, description)]`。

### 3.3 适合什么场景

优先用 `MethodToolWrapper` 的典型场景：

- 读写宿主状态
- 查询索引 / 检索文档 / 调系统服务
- 需要访问 `context.Services` / `context.Items`
- 需要返回不仅仅是“接受/拒绝”而是完整业务结果文本

如果你已经拿着一个已标 `[Tool]` 的方法委托，也可以直接用：

```csharp
var echoTool = MethodToolWrapper.FromDelegate<EchoInput>(host.EchoAsync);
```

---

## 4. 结构化产物：`ArtifactToolWrapper<T>`

`ArtifactToolWrapper<T>` 适合“模型提交一个结构化产物，然后宿主决定接不接受”。

它不是把 `T` 当普通工具参数来处理，而是把“产物本身”作为主角：

- schema 从 `T` 反射生成
- raw JSON 先过 schema 解析、反序列化、DataAnnotations 校验
- 全都成功后才把 `T` 交给你的 handler

### 4.1 最小示例

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

[Description("Draft outline submitted by the model.")]
public sealed class OutlineDraft {
    [Description("Document title.")]
    [MinLength(3)]
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [Description("Top-level sections.")]
    [JsonPropertyName("sections")]
    public IReadOnlyList<string> Sections { get; init; } = [];
}

var acceptedDrafts = new List<OutlineDraft>();

var submitDraft = ArtifactToolWrapper<OutlineDraft>.Create(
    "draft.submit",
    (draft, context) => {
        acceptedDrafts.Add(draft);
        return new ValidateResult(true, $"saved:{draft.Title}|{context.ExecutionSequence}");
    }
);

var executor = new ToolExecutor(
    new ToolRegistry([submitDraft]),
    new ToolSessionState()
);

var execution = await executor.ExecuteAsync(
    new RawToolCall(
        "draft.submit",
        "call-2",
        """{"title":"Atelia Tools","sections":["Why","How"]}"""
    ),
    CancellationToken.None
);

Console.WriteLine(execution.ExecuteResult.GetFlattenedText());
// saved:Atelia Tools|1
```

### 4.2 什么时候优先用它

优先用 `ArtifactToolWrapper<T>` 的场景：

- 让模型交一份 outline / patch plan / analysis report
- 让模型做结构化抽取
- 让模型给出排序、打标签、分类结果
- 你真正想保留的是一个 `T`，不是一串自由文本

和 `MethodToolWrapper` 的关键区别是：

- `MethodToolWrapper` 更像“调用宿主能力”。
- `ArtifactToolWrapper<T>` 更像“向宿主提交结构化作品”。

### 4.3 handler 只看见“已经过关”的 `T`

`ArtifactToolWrapper<T>` 的 handler 只有在下面三关都通过后才会被调用：

1. raw JSON 能按 schema 解析
2. JSON 能反序列化成 `T`
3. `T` 通过 DataAnnotations 校验

因此如果模型多传未知字段、漏必填字段、或产物对象不满足 `[MinLength]` / `[Range]` 等约束，handler 根本不会执行，工具会直接返回 `Failed`。

---

## 5. `ToolExecutor` 怎么接进 Completion 主循环

`ToolExecutor` 有两个直接用途：

1. 把当前 session 可见的工具投影成 `CompletionRequest.Tools`
2. 把模型返回的 `RawToolCall` 执行成 `ToolResult`，再回灌成 `ToolResultsMessage`

最小接法是：

```csharp
var executor = new ToolExecutor(registry, session);

var history = new List<IHistoryMessage> {
    new ObservationMessage("Read README and propose an outline."),
};

var request = new CompletionRequest(
    ModelId: "Qwen3.5-27b-GPTQ-Int4",
    SystemPrompt: "You are a helpful coding agent.",
    Context: history,
    Tools: executor.VisibleToolDefinitions
);

var completion = await client.StreamCompletionAsync(request, null, cancellationToken);
history.Add(completion.Message);

if (completion.Message.ToolCalls.Count > 0) {
    var toolResults = new List<ToolResult>();

    foreach (var call in completion.Message.ToolCalls) {
        var execution = await executor.ExecuteAsync(call, cancellationToken);
        toolResults.Add(execution.ToToolResult());
    }

    history.Add(new ToolResultsMessage(content: null, results: toolResults));
}
```

这里的桥接关系非常重要：

- `completion.Message.ToolCalls` 给你 `RawToolCall`
- `ToolExecutor.ExecuteAsync(...)` 给你 `ToolCallExecutionResult`
- `ToolCallExecutionResult.ToToolResult()` 变成可回灌的 `ToolResult`
- `new ToolResultsMessage(null, results)` 再喂回下一轮 `CompletionRequest.Context`

如果你已经在用 `Atelia.Completion`，这一段就是 tool loop 的主拼接点。

---

## 6. session 可见性与运行时注入

`ToolSessionState` 负责当前 session 的三件事：

- `ToolAccessPolicy`：哪些工具对当前模型可见 / 可执行
- `Services`：工具运行时依赖的服务容器
- `Items`：轻量 session 数据，比如 scope、repo root、当前用户上下文

示例：隐藏某个工具

```csharp
var session = new ToolSessionState(
    toolAccess: new ToolAccessPolicy(hiddenToolNames: ["internal.only"]),
    items: new Dictionary<string, object?> { ["scope"] = "review" }
);
```

此时：

- `executor.VisibleToolDefinitions` 不会把 `internal.only` 暴露给模型
- 即使模型伪造了 `RawToolCall("internal.only", ...)`，`ExecuteAsync(...)` 也会返回 `Failed`，而不是执行成功

这是一层简单但有用的 defense in depth。

---

## 7. DTO / Artifact 声明约束

这套反射式声明器故意不是“任意 CLR 类型都能自动映射”。当前主线建议把输入对象控制在它自然擅长的范围内。

### 7.1 已支持的主路径

- 根类型必须是 concrete `class` / `record class`
- 标量支持：`string`、`bool`、`int`、`long`、`double`、非 `[Flags]` enum
- 集合支持：数组、`List<T>`、`IReadOnlyList<T>`
- 对象支持：嵌套 object
- 元数据支持：`[Description]`、`[JsonPropertyName]`、`[Required]`、`[Range]`、`[StringLength]`、`[MinLength]`、`[RegularExpression]`

### 7.2 当前不适合的类型

- 循环对象图
- nullable object / nullable array / nullable collection element
- `[Flags]` enum
- 条件式 `JsonIgnore(Condition = ...)`
- 需要自定义反序列化协议的复杂 union

如果你的输入类型明显超出上面范围，直接手写 `ITool` 往往更清晰。

---

## 8. 常见坑

1. `MethodToolWrapper` 的方法签名不对：必须正好是“一个业务输入对象 + `ToolExecutionContext` + `CancellationToken`”，返回 `ValueTask<ToolExecuteResult>`。
2. 把 DTO 的 `[Description]` 当成 tool description：`MethodToolWrapper` 的 tool description 来自 `[Tool]`，不是 DTO 根类型的 `[Description]`。
3. 模型多传未知字段：schema 默认 `additionalProperties: false`，会直接触发解析失败。
4. 以为 validation 失败后 handler 还会跑：不会。`MethodToolWrapper` 和 `ArtifactToolWrapper<T>` 都会在调用业务逻辑前拦住。
5. 用大小写不同的重复 JSON 名：反射 schema 时会直接抛错。
6. 想把 `ToolExecutionContext` 里的运行时状态偷偷放到 schema 里：不应该。它本来就是 runtime-only 参数。

---

## 9. 何时不该用 wrapper

下面这些情况，通常应该直接手写 `ITool`：

- 你需要完全自定义的参数协议
- 你要处理超出反射声明器能力边界的类型
- 你要返回非文本 `ToolResultBlock`（未来若扩展）或更特别的执行语义
- 你根本不需要反射 schema，只想手写 `ToolDefinition`

如果你只需要“从一个类型生成 `ToolDefinition`”，但不需要可执行工具，可以直接用：

```csharp
var definition = ReflectedToolDefinitionBuilder.BuildDefinitionUsingTypeDescription<MyInput>("tool.name");
```

---

## 10. 可运行样例

本 README 中的主路径样例已经落实成可执行测试，方便以后改 API 时及时发现文档漂移：

- `tests/Completion.Tests/Tools/CompletionToolsQuickStartSamplesTests.cs`
- `tests/Completion.Tests/Tools/MethodToolWrapperTests.cs`
- `tests/Completion.Tests/Tools/ArtifactToolWrapperTests.cs`
- `tests/Completion.Tests/Tools/ToolExecutorTests.cs`

只跑这批样例可用：

```bash
dotnet test tests/Completion.Tests/Completion.Tests.csproj --filter "FullyQualifiedName~Completion.Tools"
```
