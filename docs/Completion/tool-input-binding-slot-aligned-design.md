# Completion Tool Input Binding 设计草案

> **状态**：设计中
> **最后更新**：2026-05-22
> **直接动机**：为 `MethodToolWrapper` 与 `ArtifactToolWrapper<T>` 提供更一致的输入绑定基础层，并逐步藏掉 `IReadOnlyDictionary<string, object?>` 这种过渡中间态。

---

## 1. 结论摘要

建议引入一层新的共享绑定结果模型，其核心不是“另一个字典”，而是**按 `ToolSchema.Object.Properties` 顺序对齐的 `BoundObject`**。

关键约束是：

- `BoundObject.Properties[i]` 永远对应 `BoundObject.Schema.Properties[i]`
- 顶层与嵌套 object 都遵守同一约束
- 绑定结果显式表达 object / array / scalar 三种形态
- 路径、原始 JSON 片段、schema 信息随绑定结果一同保留

这意味着：

- `MethodToolWrapper` 不再依赖 `IReadOnlyDictionary<string, object?>` 取参
- `ArtifactToolWrapper<T>` 与 `MethodToolWrapper` 可以共享 schema 校验、raw JSON 归一化、parse warning / parse failure 的处理框架
- 但**本阶段不要求**把 method 参数升级为真正的动态 CLR host 类型，也不要求立刻让 artifact 反序列化改走同一套 object projector

换句话说，这个设计的重点是先统一“绑定完成态”，而不是一步到位统一“最终执行投影器”。

---

## 2. 当前问题

当前主线已经完成：

- `ITool.ExecuteAsync(...)` 统一接收 `RawToolCall`
- `ToolExecutor` 已收缩为纯调度器
- `MethodToolWrapper` / `ArtifactToolWrapper<T>` 各自在内部消费 raw JSON

但两条执行链的中间绑定层仍然不一致：

- `MethodToolWrapper`
  - `RawToolCall`
  - `JsonArgumentParser.ParseArguments(...)`
  - `IReadOnlyDictionary<string, object?>`
  - `object?[]`
  - delegate 调用
- `ArtifactToolWrapper<T>`
  - `RawToolCall`
  - `JsonArgumentParser.ParseArguments(...)`
  - 原始 JSON 直接 `JsonSerializer.Deserialize<T>(...)`
  - DataAnnotations / handler validation

其中 `MethodToolWrapper` 的 `Dictionary<string, object?>` 中间态有几个问题：

- 它是松散的名字查找结构，不表达 object / array / scalar 的显式形态
- 它只勉强适合“顶层 flat 参数 -> 方法签名”的消费方式
- 它把路径、schema、raw 片段、presence 这些重要语义分散在外部逻辑里
- 它不适合作为未来共享验证层的长期核心表示

所以这一步真正想解决的问题不是“字典性能不好”，而是“绑定结果缺乏稳定、显式、可扩展的结构语义”。

---

## 3. 设计目标

### 3.1 主目标

- 为 tool 输入建立一个共享的、结构化的 binding result
- 保留当前 `JsonArgumentParser` 已有的错误路径与 warning 语义
- 为 `MethodToolWrapper` 提供比 dictionary 更稳定的参数读取方式
- 为将来的共享 validation / projector 层提供自然落点

### 3.2 非目标

本设计**暂不追求**：

- 动态生成 CLR arguments host 类型
- 用 Source Generator 取代 runtime 反射路径
- 让 `ArtifactToolWrapper<T>` 不再使用 `JsonSerializer.Deserialize<T>`
- 立刻为所有绑定路径做极限性能优化
- 扩展 `MethodToolWrapper` 去支持任意复杂 CLR 方法签名

---

## 4. 建议模型

## 4.1 总体结构

建议引入类似这样的内部模型：

```csharp
internal sealed record ToolBindingResult(
    BoundObject? Root,
    ImmutableDictionary<string, string> RawArguments,
    string? ParseError,
    string? ParseWarning
);

internal abstract record BoundValue(
    ToolSchema Schema,
    string Path,
    string RawJson
);

internal sealed record BoundScalar(
    ToolSchema.Value Schema,
    string Path,
    string RawJson,
    object? Value
) : BoundValue(Schema, Path, RawJson);

internal sealed record BoundArray(
    ToolSchema.Array Schema,
    string Path,
    string RawJson,
    ImmutableArray<BoundValue?> Items
) : BoundValue(Schema, Path, RawJson);

internal sealed record BoundObject(
    ToolSchema.Object Schema,
    string Path,
    string RawJson,
    ImmutableArray<BoundPropertySlot> Properties,
    ImmutableDictionary<string, BoundValue>? AdditionalProperties
) : BoundValue(Schema, Path, RawJson);

internal sealed record BoundPropertySlot(
    ToolSchema.Property SchemaProperty,
    bool IsPresent,
    BoundValue? Value
);
```

这里只是草图，字段名可以调整，但核心约束建议保持不变。

## 4.2 核心不变式

最关键的不变式是：

- `BoundObject.Properties.Length == BoundObject.Schema.Properties.Length`
- 对任意 `i`，`BoundObject.Properties[i].SchemaProperty == BoundObject.Schema.Properties[i]`
- 若 property 在输入 JSON 中缺失，则对应 slot 仍存在，只是 `IsPresent = false`
- 若 property 出现且值合法，则 `IsPresent = true` 且 `Value != null` 或明确表示 null scalar / null array

这个设计比 `Dictionary<string, object?>` 更重要的一点是：

- **“缺失”与“出现但值为 null”可以自然区分**

这对于 optional/default/nullability 处理很关键。

## 4.3 为什么用 slot 而不是 property name map

因为 schema 本身已经提供了稳定顺序：

- `MethodToolWrapper` 顶层参数 schema 是按方法参数顺序构造的
- `ReflectedToolDefinitionBuilder` 产出的 object schema 也有稳定的 `Properties` 顺序

因此让 binding result 与 schema 顺序对齐，有几个直接好处：

- `MethodToolWrapper` 可以按 index 读取，而不是按名字查找
- 后续可为 projector 编译出基于 index 的快速读取逻辑
- 可减少“lookup by string”作为长期运行时热点
- 嵌套 object 的结构也更规整，不必临时把字典再翻译成数组或 slot

若确实需要名字查找，可以在消费层做辅助方法，但不建议把“名字 map”继续作为绑定层的主形态。

---

## 5. 绑定流程建议

建议把当前 `JsonArgumentParser` 逐步演进为一个更明确的 binder：

```text
RawToolCall.RawArgumentsJson
    -> normalize raw text ("{}" fallback)
    -> parse JSON document
    -> walk ToolSchema.Object
    -> produce ToolBindingResult
        - Root: BoundObject
        - RawArguments
        - ParseError
        - ParseWarning
```

这里有两个重要点：

### 5.1 ParseError / ParseWarning 语义要保留

当前系统已经有比较稳定的语义：

- `ParseError`
  - 类型不匹配
  - required 缺失
  - enum / range / pattern 不满足
  - unknown property 等
- `ParseWarning`
  - 例如 `float64 -> float32` 的精度损失

新 binder 不应为了换一种内部结构而破坏这些语义。

### 5.2 `RawArguments` 仍建议保留

即便未来主消费方从 dictionary 迁走，`RawArguments` 这种“路径 -> 原始片段”仍有实际价值：

- 失败信息拼接
- 调试输出
- 将来更细粒度的验证或错误定位

因此建议把 `RawArguments` 视作 binding result 的附属元数据，而不是旧时代遗留物。

---

## 6. 各工具如何消费

## 6.1 `MethodToolWrapper`

建议把它拆成两层：

- `ToolInputBinder`
  - 负责 `RawToolCall -> ToolBindingResult`
- `MethodInvocationProjector`
  - 负责 `BoundObject -> object?[]` 或直接 `-> delegate call`

第一版完全可以保持朴素：

- 读取顶层 `BoundObject.Properties[i]`
- 对应到第 `i` 个方法参数
- `IsPresent=false` 时应用默认值或报错
- `BoundScalar.Value` 直接用于构造 `object?[]`

这样做的价值不在于立刻“更快”，而在于：

- `MethodToolWrapper` 不再直接依赖松散 dictionary
- 默认值、presence、nullability 的处理边界更清楚

第二版若需要优化，再考虑在 wrapper 构造时编译：

- `Func<BoundObject, object?[]>`
- 或 `Func<BoundObject, CancellationToken, ValueTask<ToolExecuteResult>>`

也就是说，**ExpressionTree 的更自然落点是 projector，而不是 binder 本身**。

## 6.2 `ArtifactToolWrapper<T>`

本阶段不建议强行让它改成 `BoundObject -> T`。

更稳妥的做法是：

- 先共享 `ToolInputBinder`
- 仍然保留当前 `JsonSerializer.Deserialize<T>(rawJson)` 主路径
- 让 schema 校验、parse failure / warning、raw normalization 这些前置步骤共享

这样可以先把 Method / Artifact 两条链的“前半段”统一起来，而不急着统一“最后一公里”的 CLR 对象投影方式。

等将来确实需要更深的共享层，再讨论是否做：

- `BoundObject -> T` 的共享 model binder
- 或动态 host / source-generated binder

---

## 7. 与 `JsonElement`/`JsonNode` 方案对比

另一条显而易见的方案是把当前 dictionary 的 value 从 `object?` 改成 `JsonElement` 或 `JsonNode`。

这条路线的优点：

- 改动小
- 能避免过早数值装箱
- 能保留更多原始 JSON 语义

但它的问题是：

- 仍然是“名字 -> 值”的松散结构
- 不自然表达“缺失 vs present null”
- `JsonElement` 有 `JsonDocument` 生命周期问题
- `MethodToolWrapper` 仍然要自己做第二次映射
- 对共享 validation 层的帮助有限

所以如果目标只是“小修补”，`JsonElement` 方案可以考虑；
但如果目标是“为下一阶段共享绑定/验证层打地基”，slot-aligned `BoundObject` 更合适。

---

## 8. 与动态 arguments host 方案对比

更激进的方案是直接运行时生成一个 arguments host 类型，让 method 参数也像 artifact model 那样反序列化成强类型对象。

这条路线长期可能有吸引力，但当前阶段不建议优先采用，原因是：

- 动态类型生成、缓存、调试都更复杂
- 要复制 enough metadata 才能让 validation 真正自然
- 一旦走太深，容易把“共享绑定层”与“最终执行投影器”绑死在一起

相较之下，slot-aligned `BoundObject` 的优点是：

- 先统一 binding result
- 不阻碍未来演化到动态 host
- 出问题时更容易排查

它更像一个保守但有前景的中间台阶。

---

## 9. 真实收益评估

这里需要冷静看待，不要把它神化。

### 9.1 明确能得到的收益

如果落地这个设计，比较确定的收益是：

- `MethodToolWrapper` 摆脱 `Dictionary<string, object?>` 这一松散中间态
- `MethodToolWrapper` / `ArtifactToolWrapper<T>` 前半段执行链更一致
- “缺失 / null / 默认值” 语义表达更明确
- 为后续共享 validation / projector 层提供稳定数据结构
- 将来若做 projector 编译优化，有更自然的 index-based 落点

这些收益主要是**架构清晰度收益**与**后续重构收益**，不是眼前就会非常显著的性能收益。

### 9.2 没那么明确的收益

以下收益目前都不应过度承诺：

- 明显降低整体分配
- 明显提升 tool 执行吞吐
- 明显简化 `ArtifactToolWrapper<T>` 当前实现
- 立刻减少大量代码量

原因很简单：

- 当前 tool 调用频率本身未必高到需要极致优化
- 真正重的部分常常仍在 JSON 解析、反序列化、业务 handler
- 新 binder 自身也会引入一套新的对象模型与测试成本

所以如果把这项重构卖点表述为“性能升级”，目前是不扎实的。

### 9.3 最适合推动它的理由

最适合推动它的理由应该是：

- 它能把当前执行链中一个明显过渡态收口掉
- 它为后续“共享验证层”建立清晰边界
- 它让未来是否走动态 host / projector compile 变成局部优化问题

也就是说，它最大的价值是**为下一阶段减小设计不确定性**。

### 9.4 什么情况下不值得做

如果接下来数轮里：

- `ArtifactToolWrapper<T>` 继续以 `JsonSerializer.Deserialize<T>` 为主，不急着进一步统一
- `MethodToolWrapper` 仍长期只支持简单 flat 标量参数
- 项目近期更需要推进上层业务能力，而不是继续打磨底层执行框架

那么这项设计的现实收益就会偏弱。

在这种情况下，更小的替代方案可能已经够用：

- 保持当前 binder 结构
- 只把 dictionary value 改为更少信息损失的表示
- 或只在 `MethodToolWrapper` 内部把 dictionary 藏得更深

换句话说，这个设计**不是“现在非做不可”**，而是“如果我们确认下一阶段要继续收口共享 binding / validation 层，它就很值得做”。

---

## 10. 建议的推进顺序

若决定推进，建议按最小增量做：

### 10.1 第一步：只引入 `ToolBindingResult` / `BoundObject`

- 不动 `ArtifactToolWrapper<T>` 的最终反序列化策略
- 不做动态 host
- 不做 projector 编译

目标只是让 `JsonArgumentParser` 的输出从 dictionary 升级为结构化 binding result。

### 10.2 第二步：让 `MethodToolWrapper` 改消费 `BoundObject`

- 去掉 `ArgGetter.GetValue(IReadOnlyDictionary<string, object?>?)`
- 改为基于 slot 的参数投影

### 10.3 第三步：抽共享前置执行框架

- raw normalization
- schema bind
- parse failure
- parse warning attach

让 `MethodToolWrapper` / `ArtifactToolWrapper<T>` 共享这部分外壳。

### 10.4 第四步：再决定是否需要更深优化

这时再评估是否有必要做：

- projector 编译缓存
- `BoundObject -> T` model binder
- 动态 arguments host
- source generator

---

## 11. 最终建议

这个设计方向在**架构上合理、技术上可行**，但它的收益更偏“结构清晰与后续演进友好”，而不是“立刻带来显著性能或显著简化”。

因此更合适的定位是：

- 把它当作“下一阶段共享 binding / validation 层”的候选地基
- 先小步实现第一阶段，再看真实收益
- 不要在还没验证效果前，一次性投入动态 host / source generator 等更复杂路线

一句话总结：

**slot-aligned `BoundObject` 是一个不错的中间层候选，但它真正值钱的前提，是我们接下来确实要继续推进 Method / Artifact 的共享绑定与验证框架。**
