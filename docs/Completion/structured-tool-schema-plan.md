# Completion 结构化 tool schema 方案（历史归档，阶段 1 已落地）

> **归档说明**：这份文档记录的是阶段 1 的设计与落地过程，不是现行 API 指南。
> **截至 2026-05-21 的现状**：`ITool` 只剩 `Definition`，`ToolDefinition` 只剩 `Name / Description / InputSchema`，schema 真源只剩 `ToolSchema`。下文若出现 `ToolParamSpec` / `CreateFlat(...)` / `Parameters`，均属于历史语境。
> **原始目标**：先落地“`record class` / `class` + Attribute -> `ToolDefinition`”这条声明链路，作为后续“验证型 structured artifact tool”的基础。
> **阶段 1 边界（历史）**：当时只做声明侧 schema / `ToolDefinition` 生成；不做执行、验证、收集与 session state。
> **最后更新**：2026-05-21

---

## 1. 本轮已收口的主线

这轮已经把下面这条主线落地完成：

- `ToolDefinition` 的 provider-facing 声明真源从旧的 flat `ToolParamSpec[]` 收口到 `InputSchema`
- `InputSchema` 采用最小递归 `ToolSchema` tree，可表达 `object / array / value`
- `JsonToolSchemaBuilder` 已改为直接从 `ToolDefinition.InputSchema` 生成 provider 请求里的 JSON Schema
- 新增 `ReflectedToolDefinitionBuilder`，可从带 Attribute 的 `class` / `record class` 生成递归 `ToolDefinition`
- 当时仍保留过渡性的 flat 构造入口；该兼容层现已删除

这轮明确**没有**做：

- 不新增 `GetStructuredAsync<T>` 一类绕开 tool loop 的 API
- 不把 reflected declaration 自动接成默认 `ITool` / `ToolExecutor` 执行主路径
- 不实现 structured artifact 的验证 / acceptance / collection / replay policy
- 不收口 provider-facing nullable JSON Schema 的最终表达方式

也就是说，当前已经解决的是“如何声明递归 tool schema”，还没有解决“如何把这份声明自动变成一个带执行/验证闭环的 structured tool”。

---

## 2. 已落地的关键设计决策

### 2.1 `ToolDefinition` 根 schema 固定为 object

`ToolDefinition` 构造函数接受 `ToolSchema inputSchema`，但会强制要求根节点必须是 `ToolSchema.Object`。

原因保持不变：

- tool call arguments 在各家 provider 上本质上都是一个 JSON object
- 能和现有 provider 投影天然对齐
- 后续若做 structured artifact tool，也更适合作为“提交一个对象”的统一入口

### 2.2 `ToolDefinition.Name` 继续显式传入

tool name 不从类型名或 Attribute 自动推断，而是由 builder 调用方显式传入：

```csharp
var definition = ReflectedToolDefinitionBuilder.Build<IssueTriage>(
    "emit_issue_triage"
);
```

这样可以清楚分离：

- CLR 类型名
- 对外协议名
- prompt / tool-call 中使用的稳定名字

### 2.3 `ToolSchema` 采用封闭递归 tree

当前落地形态在 [ToolDefinition.cs](/repos/focus/atelia/prototypes/Completion.Abstractions/ToolDefinition.cs)：

```csharp
public abstract record class ToolSchema(string? Description = null, string? Example = null) {
    public sealed record class Property(string Name, ToolSchema Schema, bool IsRequired);
    public sealed record class Object(...) : ToolSchema;
    public sealed record class Array(...) : ToolSchema;
    public sealed record class Value(...) : ToolSchema;
}
```

其中：

- `ToolSchema.Object` 表达 object 节点
- `ToolSchema.Array` 表达数组节点
- `ToolSchema.Value` 表达标量节点，底层复用现有 `ToolParamType`
- `ToolSchema.Property.IsRequired` 表达“字段是否必须出现”

当前实现没有额外引入 `ScalarKind`，而是直接复用 `ToolParamType`。

### 2.4 第一阶段只支持 `class` / `record class`

`ReflectedToolDefinitionBuilder` 当前 API 约束为：

```csharp
public static ToolDefinition Build<TInput>(string toolName)
    where TInput : class;

public static ToolDefinition Build(string toolName, Type inputType);
```

builder 会 fail-fast 拒绝：

- `struct` / `record struct`
- abstract class
- interface
- `string` 这种非 object 根类型

---

## 3. 当前类型与 API 轮廓

### 3.1 `ToolDefinition`（阶段 1 API 快照，非现行）

阶段 1 当时 `ToolDefinition` 的核心形态是：

```csharp
public sealed record class ToolDefinition {
    public ToolDefinition(string name, string description, ToolSchema inputSchema);

    public string Name { get; }
    public string Description { get; }
    public ToolSchema InputSchema { get; }
    public ImmutableArray<ToolParamSpec> Parameters { get; }

    public static ToolDefinition CreateFlat(
        string name,
        string description,
        IReadOnlyList<ToolParamSpec>? parameters = null
    );
}
```

说明（历史）：

- `InputSchema` 是声明真源
- `Parameters` 只是兼容投影件，给剩余 flat 展示/旧路径使用
- 若 schema 无法被稳定投影回 flat 参数，`Parameters` 会 fail-closed 返回空
- `CreateFlat(...)` 是旧工具定义的过渡入口

当前现状已经进一步收口为：

```csharp
public sealed record class ToolDefinition {
    public ToolDefinition(string name, string description, ToolSchema inputSchema);

    public string Name { get; }
    public string Description { get; }
    public ToolSchema InputSchema { get; }
}
```

### 3.2 `ReflectedToolDefinitionBuilder`

当前 builder 已放在 [ReflectedToolDefinitionBuilder.cs](/repos/focus/atelia/prototypes/Completion.Tools/Declaration/ReflectedToolDefinitionBuilder.cs)：

```csharp
public static class ReflectedToolDefinitionBuilder {
    public static ToolDefinition Build<TInput>(string toolName)
        where TInput : class;

    public static ToolDefinition Build(string toolName, Type inputType);
}
```

当前行为：

- 根类型必须是 concrete `class` / `record class`
- 根类型必须带 `[Description]`
- 读取 public instance property
- 支持 positional record 上的 `[property: ...]` Attribute
- 检测循环引用并 fail-fast
- 检测大小写不敏感重名并 fail-fast

### 3.3 provider schema 投影

[JsonToolSchemaBuilder.cs](/repos/focus/atelia/prototypes/Completion/Utils/JsonToolSchemaBuilder.cs) 已直接消费 `ToolDefinition.InputSchema`：

- `ToolSchema.Object` -> JSON Schema `type: object`
- `ToolSchema.Array` -> JSON Schema `type: array`
- `ToolSchema.Value` -> JSON Schema `type: string / boolean / integer / number`
- string enum / `minLength` / `maxLength` / `pattern` / `minimum` / `maximum` 已可投影

这意味着只要调用方把生成的 `ToolDefinition` 放进 `CompletionRequest.Tools`，provider 请求侧已经能看到递归 schema。

需要注意的是：

- 这只是“声明投影”打通了
- 这里描述的是阶段 1 当时状态；当前执行主路径已改为 schema-driven，不再走 `ITool.Parameters`

---

## 4. Attribute 映射规则

阶段 1 优先复用 .NET / `System.Text.Json` 现成 Attribute，不额外发明一套新注解。

### 4.1 类型级 Attribute

支持：

- `[Description]`

用途：

- 作为 `ToolDefinition.Description`

规则：

- 根类型缺少 `[Description]` 时直接抛错
- 嵌套 object 的类型级 `[Description]` 是可选的；属性自己的 `[Description]` 优先作为该 object property 的 description

### 4.2 属性级 Attribute

当前支持：

- `[Description]`
- `[JsonPropertyName]`
- `[JsonIgnore]`
- `[Required]`
- `[Range]`
- `[StringLength]`
- `[RegularExpression]`

映射规则：

- `[Description]` -> property schema 的 `description`
- `[JsonPropertyName]` -> schema 中的字段名
- `[JsonIgnore]` -> 跳过属性
- `[Required]` -> 强制 `IsRequired = true`
- `[Range]` -> 数值 `minimum` / `maximum`
- `[StringLength]` -> `minLength` / `maxLength`
- `[RegularExpression]` -> `pattern`

### 4.3 `JsonIgnore` 当前是保守支持

当前实现只接受：

- 无 `Condition` 的 `[JsonIgnore]`
- 或等价的 `Condition = JsonIgnoreCondition.Always`

以下情况会 fail-fast：

- `JsonIgnoreCondition.WhenWritingNull`
- `JsonIgnoreCondition.WhenWritingDefault`
- 其他非 `Always` 条件

因为这些条件属于“运行时序列化策略”，而当前 builder 做的是“静态声明收口”，两者暂不混用。

### 4.4 required / optional 的当前规则

当前 `ReflectedToolDefinitionBuilder` 对属性是否 required 的判断是：

1. 若属性被 `[JsonIgnore]` 跳过，则不进入 schema
2. 若有 `[Required]`，则 `IsRequired = true`
3. 否则若该属性“允许 null”，则 `IsRequired = false`
4. 否则 `IsRequired = true`

这里的“允许 null”当前用于**是否可省略**的判定来源，具体包括：

- `Nullable<T>`
- 可空引用类型

### 4.5 nullable 的当前阶段性状态

这是当前主线里特意**暂时搁置**的一块：

- `ToolSchema.Value` / `ToolSchema.Array` 类型本身带有 `IsNullable`
- 但 provider-facing JSON Schema 目前**还没有**完整表达“字段显式传 `null`”的最终语义
- `ReflectedToolDefinitionBuilder` 当前主要利用 nullability 做 `required / optional` 判定
- nullable array / nullable object 当前直接不支持，会 fail-fast

所以这轮要把语义理解成：

- “可空”目前主要帮助我们判断字段能否省略
- “显式传 `null` 到 provider schema 应如何声明”留到后续单独收口

---

## 5. 支持矩阵（与当前实现一致）

### 5.1 当前支持的 CLR 类型

| CLR 类型 | schema 形态 | 支持情况 | 备注 |
|---|---|---|---|
| `string` | `ToolSchema.Value(String)` | 支持 | 可叠加 `StringLength` / `RegularExpression` |
| `bool` | `ToolSchema.Value(Boolean)` | 支持 | |
| `int` | `ToolSchema.Value(Int32)` | 支持 | 可叠加 `Range` |
| `long` | `ToolSchema.Value(Int64)` | 支持 | 可叠加 `Range` |
| `double` | `ToolSchema.Value(Float64)` | 支持 | 可叠加 `Range` |
| 普通 enum | `ToolSchema.Value(String, StringEnumValues=...)` | 支持 | 按 string enum 投影 |
| `T[]` | `ToolSchema.Array` | 支持 | `T` 必须本身受支持 |
| `List<T>` / `IReadOnlyList<T>` | `ToolSchema.Array` | 支持 | `T` 必须本身受支持 |
| 嵌套 `class` / `record class` | `ToolSchema.Object` | 支持 | 递归读取 public instance properties |

### 5.2 当前明确不支持的类型 / 形态

| CLR 类型 / 形态 | 当前处理 |
|---|---|
| `struct` / `record struct` | 不支持，fail-fast |
| `float` | 不支持 |
| `decimal` | 不支持 |
| `Dictionary<string, T>` | 不支持 |
| `object` | 不支持 |
| 抽象类 / 接口 / 多态层级 | 不支持 |
| `HashSet<T>` / `IEnumerable<T>` / 其他宽泛集合 | 不支持 |
| 多维数组 | 不支持 |
| `Flags enum` | 不支持，fail-fast |
| 循环引用对象图 | 不支持，fail-fast |
| 字段（field） | 不支持，只读 property |
| nullable array / nullable object | 不支持，fail-fast |
| nullable collection element | 不支持，fail-fast |

### 5.3 Attribute 支持矩阵

| Attribute | 当前状态 | 说明 |
|---|---|---|
| `DescriptionAttribute` | 支持 | 根类型描述必填；属性描述可选，缺省时回退属性名 |
| `JsonPropertyNameAttribute` | 支持 | 指定字段名 |
| `JsonIgnoreAttribute` | 有条件支持 | 仅支持默认 / `Always` |
| `RequiredAttribute` | 支持 | 强制 required |
| `RangeAttribute` | 支持 | 数值上下界 |
| `StringLengthAttribute` | 支持 | 字符串长度范围 |
| `RegularExpressionAttribute` | 支持 | 字符串正则 |
| `DefaultValueAttribute` | 不支持 | 尚未收口 default 的声明语义 |
| `DisplayAttribute` | 不支持 | 暂不引入 UI 语义 |
| `JsonConverterAttribute` | 不支持 | 尚未与序列化契约整合 |

---

## 6. 示例（按当前 API）

### 6.1 声明输入类型

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

[Description("Search documentation with structured filters.")]
public sealed record class SearchDocsRequest(
    [property: Description("Query text.")]
    [property: StringLength(50, MinimumLength = 3)]
    [property: RegularExpression("^[a-z ]+$")]
    [property: JsonPropertyName("q")]
    string Query,

    [property: Description("Search mode.")]
    SearchMode Mode,

    [property: Description("Structured filters.")]
    SearchFilters Filters,

    [property: Description("Requested tags.")]
    IReadOnlyList<string> Tags,

    [property: Description("Maximum result count.")]
    [property: Required]
    [property: Range(1, 10)]
    int? Limit
);

public enum SearchMode {
    Exact,
    Fuzzy
}

public sealed class SearchFilters {
    [Description("Cursor offset.")]
    [Range(typeof(long), "0", "99")]
    public long Cursor { get; init; }

    [Description("Score threshold.")]
    [Range(0.1, 1.0)]
    public double Threshold { get; init; }

    public string? Label { get; init; }
}
```

### 6.2 生成 `ToolDefinition`

```csharp
var definition = ReflectedToolDefinitionBuilder.Build<SearchDocsRequest>(
    "search_docs"
);
```

### 6.3 当前实际 schema 要点

应得到一个根 object schema，包含：

- `q` -> string required，带 `minLength` / `maxLength` / `pattern`
- `Mode` -> string enum required
- `Filters` -> object required
- `Tags` -> array required
- `Limit` -> integer required，带 `minimum = 1` / `maximum = 10`
- `Filters.Label` -> string optional

并且：

- `additionalProperties = false`
- 根描述来自类型级 `[Description]`
- `Filters` 的描述来自属性级 `[Description("Structured filters.")]`

注意：

- `Label` 虽然是 `string?`，当前只意味着它是 optional；provider schema 还没有完整表达“显式传 null”的最终语义
- `int? Limit` 之所以仍是 required，是因为显式加了 `[Required]`

---

## 7. 第一阶段明确不做什么

### 7.1 不做 structured artifact 执行闭环

暂不引入：

- “验证型 artifact tool”的 `ExecuteAsync`
- 校验失败后的 tool result 约定格式
- 自动 repair 循环
- artifact acceptance / rejection 语义

### 7.2 不做 artifact 收集与会话态

暂不引入：

- `StructuredArtifactCollector`
- 保序异构 artifact transcript
- `GetAccepted<T>()` / `TryGetSingle<T>()`
- completion policy / session state

### 7.3 不做更强泛化 schema 特性

暂不引入：

- `$ref`
- `oneOf` / `anyOf`
- discriminator
- dictionary key schema
- 自定义 `JsonConverter` 感知
- source generator

### 7.4 不把默认执行链强行升级到递归参数绑定

阶段 1 当时允许这样的“半接通状态”存在：

- provider 声明侧已经能吃递归 `InputSchema`
- 调用方也已经能从 `class` / `record class` 生成 `ToolDefinition`
- 但默认 `ToolExecutor` 仍按 `ITool.Parameters` 这条 flat 路径解析实参

这是当时的**刻意边界**，不是遗漏 bug；该边界现已收口完成。

---

## 8. 施工状态与后续入口

### 8.1 本阶段已完成

1. 在 `Completion.Abstractions` 中引入最小 `ToolSchema` tree，并把 `ToolDefinition` 真源升级为 `InputSchema`
2. 阶段 1 当时仍保留 `ToolDefinition.CreateFlat(...)` 作为 flat 参数的过渡构造入口；该入口现已删除
3. 把 `JsonToolSchemaBuilder` 改为从 `ToolDefinition.InputSchema` 生成 provider JSON Schema
4. 新增 `ReflectedToolDefinitionBuilder`，支持从 `class` / `record class` + Attribute 生成 `ToolDefinition`
5. 补齐阶段 1 的 builder / abstraction 单元测试
6. 补上 OpenAI / Anthropic / Gemini 三家 provider 的递归 schema 请求投影回归测试

### 8.2 当前残余边界

1. reflected declaration 还没有自动接成默认 `ITool`
2. 当时执行侧仍然是 flat-only；当前这一项已完成收口
3. provider-facing nullable JSON Schema 语义还未最后定稿

### 8.3 后续最自然的施工步骤

1. 可选补一条“`ReflectedToolDefinitionBuilder.Build<T>()` 产物直接喂进 provider converter”的串联测试，把 builder 与 provider projection 接缝再收紧一格
2. 再决定是否引入“验证型 structured artifact tool”这层 runtime 抽象
3. 最后单独处理 nullable 在 provider schema 中的最终表达方式
