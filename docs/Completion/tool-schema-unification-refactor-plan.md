# 历史归档：Completion ToolSchema 收口重构计划（已完成）

> **归档说明**：这份文档保留的是 ToolSchema 收口前后的设计分析，不是现行实施指南。
> **截至 2026-05-21 的现状**：`ITool` 只保留 `Definition`，`ToolDefinition` 只保留 `Name / Description / InputSchema`，schema 真源只剩 `ToolSchema`；`ToolParamSpec` / `CreateFlat(...)` / `Parameters` 已退出当前公共 API。
> **阅读提示**：下文多处出现 `ToolParamSpec`、`Parameters`、`CreateFlat(...)` 等旧名，均是在复盘当时待拆除的兼容层。
> **原始目标**：把当时分裂的 flat `ToolParamSpec` 与递归 `ToolSchema` 收口为单一 schema 体系，使 `ToolSchema` 成为声明侧与执行侧共享的唯一真源。
> **前情文档**：[structured-tool-schema-plan.md](/repos/focus/atelia/docs/Completion/structured-tool-schema-plan.md)
> **最后更新**：2026-05-21

---

## 1. 已完成状态

本计划讨论的收口工作已经完成，当前落地结果就是：

- `ToolSchema` 成为唯一的 schema 真源
- `ToolParamSpec` 已删除
- `ITool` 只保留 `Definition`
- `ToolDefinition` 只保留 `Name` / `Description` / `InputSchema`
- 执行链路已经切到 schema-driven

下文保留的是当时为何这样拆、按什么阶段推进的分析记录。

---

## 2. 为什么当时认为这个方向是合理的

### 2.1 立项时的双栈已经出现语义分叉

现在的真实状态是：

- provider-facing 声明真源已经是 `ToolDefinition.InputSchema`
- `JsonToolSchemaBuilder` 已直接消费 `ToolSchema`
- `ToolDefinition.Parameters` 只是从 `ToolSchema.Object` 向旧 flat 结构做兼容投影

这意味着 `ToolParamSpec` 已经不再是唯一真源，只是仍占据部分执行侧入口。

### 2.2 `ToolParamSpec` 的表达力天然更弱

`ToolParamSpec` 只能表达：

- 根 object 的一层 flat 标量字段
- optional 语义依赖 `defaultValue`
- 无法表达嵌套 object / array
- 无法表达“optional but no default”

这已经在当前实现里暴露出来：`ToolDefinition.TryProjectFlatParameters(...)` 对无法无损投影的 schema 会直接返回空数组，而不是继续伪装成 flat 参数。

也就是说，代码已经承认了这个事实：

- `ToolSchema` 是上位模型
- `ToolParamSpec` 是受限投影

### 2.3 双真源会持续制造维护债

当前有几个明显信号表明双栈正在制造额外复杂度：

- `ToolSchema.Value` 仍复用 `ToolParamSpec.ValidateDefaultCombination(...)`
- `ToolExecutor` / `JsonArgumentParser` / `MethodToolWrapper` 仍依赖 `ToolParamSpec`
- 文档需要反复解释“声明支持递归，但执行还是 flat-only”

如果继续这样走，后续每加一个约束字段，都要问两遍：

- `ToolSchema` 怎么表达？
- `ToolParamSpec` 要不要补？

这就是典型的双模型分裂。

---

## 3. 为什么当时认为这个方向在技术上可行

### 3.1 provider 投影已经先完成了一半

最关键的一点是：provider 侧已经不是阻塞项。

当前已经具备：

- `ToolDefinition.InputSchema` 作为 tool declaration 真源
- OpenAI / Gemini / Anthropic converter 直接消费 `ToolSchema`
- 递归 object / array / value 的 JSON Schema 投影测试

所以这次 refactor 不是“从零引入 `ToolSchema`”，而是“把剩下仍依赖 flat spec 的执行边界迁过去”。

### 3.2 执行侧也可以按 schema 递归解析

现有 `JsonArgumentParser` 的核心能力其实是：

- 校验 root 必须是 object
- 逐字段解析 JSON 值
- 处理 required / nullable / default / 类型转换 / warning / error

这些能力并不依赖 flat 这一事实本身，而是依赖：

- 有字段名
- 有字段 schema
- 能递归进入 object / array / value

也就是说，把它从 `IReadOnlyList<ToolParamSpec>` 改成 `ToolSchema.Object` 是可行的。真正要补的是：

- 递归 object / array 的 parse result 结构
- path-aware 错误信息
- unknown property / additionalProperties 的一致策略

### 3.3 当前 flat-only 工具可以先走“schema 驱动，绑定保持简单”

技术可行不等于要立刻支持“任意递归 schema 自动绑定到任意 CLR 方法签名”。

第一阶段完全可以只做到：

- 所有工具对外都暴露 `ToolDefinition`
- `ToolExecutor` 用 `ToolDefinition.InputSchema` 解析 arguments
- 现有 `MethodToolWrapper` 仍只包装 flat 方法参数，但它内部直接生成根 `ToolSchema.Object`

这样就能先完成“唯一 schema 真源”的收口，而不必同一轮就引入复杂的递归 CLR binder。

---

## 4. 立项时的阻力与真实边界

### 4.1 主要阻力不在 `Completion.Abstractions`

`Completion.Abstractions` 里的 `ToolSchema` 已经存在，真正还没迁过去的是：

- [ITool.cs](/repos/focus/atelia/prototypes/Completion.Tools/ITool.cs)
- [ToolExecutor.cs](/repos/focus/atelia/prototypes/Completion.Tools/ToolExecutor.cs)
- [JsonArgumentParser.cs](/repos/focus/atelia/prototypes/Completion.Tools/JsonArgumentParser.cs)
- [MethodToolWrapper.Impl.cs](/repos/focus/atelia/prototypes/Completion.Tools/MethodToolWrapper.Impl.cs)

换句话说，问题不在“能不能定义递归 schema”，而在“执行契约还停留在 flat 参数时代”。

与此同时，blast radius 也不只在这四个文件，还包括一批直接依赖 `ToolParamSpec` 的调用点，例如：

- `prototypes/TextAdv/*`
- `prototypes/DeepSeekDebug/Program.cs`
- `docs/Completion/quick-start.md`
- `tests/Completion.Tests/*`
- `tests/Atelia.LiveContextProto.Tests/*`

因此这次更像一次“核心契约迁移 + 外围调用点清尾”，而不是单文件重命名。

### 4.2 `ToolParamSpec` 还承担了两类职责

当前 `ToolParamSpec` 混在一起承担了：

- schema 描述职责
- 标量默认值/类型兼容校验辅助职责

如果要收口到 `ToolSchema`，就要把后者一起搬走，至少做到：

- 默认值合法性校验不再挂在 `ToolParamSpec`
- 数值/字符串约束合法性由 `ToolSchema.Value` 自己或独立 helper 负责

### 4.3 必须接受“中间阶段会有兼容层”

从技术路线看，完全可以做到最终删除 `ToolParamSpec`；但在迁移过程里，仍然需要短期兼容层：

- 文档和调用方入口需要过渡期
- `MethodToolWrapper` / 老工具实现可能先保留 flat 形状
- 某些展示逻辑如果只会显示 flat 参数，需要临时适配

关键不是“兼容层是否存在”，而是：

- 它不能再是 schema 真源
- 它必须带明确删除节点

---

## 5. 建议的目标态

### 5.1 外部类型层面

目标态建议如下：

- `ToolDefinition` 持有 `Name` / `Description` / `InputSchema`
- `InputSchema` 根节点继续固定为 `ToolSchema.Object`
- `ToolSchema` 继续承担 `Object / Array / Value / Property` 递归树
- `ToolParamSpec` 不再作为公共 schema 类型存在

### 5.2 执行契约层面

`Completion.Tools` 侧建议收口为：

- `ITool` 暴露 `ToolDefinition Definition`，而不是 `IReadOnlyList<ToolParamSpec> Parameters`
- `ToolExecutor` 从 `tool.Definition.InputSchema` 解析参数
- 参数解析器消费 `ToolSchema.Object`
- 工具执行收到的 arguments 仍可先保持 `IReadOnlyDictionary<string, object?>`

若执行侧暂不引入强类型 binder，运行时值形状需要先冻结为统一约定：

- object -> `IReadOnlyDictionary<string, object?>`
- array -> `IReadOnlyList<object?>`
- scalar -> 按 schema kind 物化为既定 CLR 标量
- `null` -> 仅在 schema 允许时出现

这里要额外定死一条规则，避免再出现“声明一套、执行一套”：

- `Definition` 必须是 authoritative metadata
- `ITool.Name` / `Description` 若继续保留，也只能派生自 `Definition`
- 不允许同时存在可独立修改的 `Definition` 与 `Parameters` / `Name` / `Description`

这里特意保守一点：

- **先统一 schema 真源**
- **后决定是否要引入更强类型的递归绑定结果**

### 5.3 flat 工具的保留方式

flat 工具不必消失，但应变成 `ToolSchema` 的一个特例，而不是另一套并行模型。

也就是说，未来 flat 工具应该是：

- 一个根 `ToolSchema.Object`
- 其 properties 全是 `ToolSchema.Value`

而不是：

- “真正的 schema 在 `ToolParamSpec[]` 里，再投影成 object schema”

---

## 6. 非目标与暂缓项

本计划建议**不**把下面这些问题绑进同一轮：

- provider-facing `null` JSON Schema 最终写法
- 从任意递归 schema 自动绑定到任意嵌套 CLR object
- 新增一整套绕过 tool loop 的 structured artifact API
- 为了命名整洁立即重命名所有 `ToolParamType`

其中最后一点尤其要克制：

- `ToolParamType` 这个名字并不完美
- 但它目前只是 `ToolSchema.Value` 的标量 kind 枚举
- 若这轮顺手大面积改名，收益远小于扰动

建议先收口语义，再评估是否将其单独重命名为 `ToolValueKind` 一类名称。

---

## 7. 分阶段实施方案

### 阶段 A：把 `ToolSchema` 明确提升为唯一真源

目标：

- 删掉 `ToolSchema.Value` 对 `ToolParamSpec` 静态 helper 的依赖
- 给 `ToolSchema` 补齐自有校验辅助
- 在文档中明确 `ToolParamSpec` 已进入废弃路径

产物：

- `ToolSchema` 自洽，不再反向引用旧 flat 类型
- `ToolDefinition.Parameters` 标注为兼容投影，准备废弃
- `ToolSchema.Object` 在构造时拒绝仅大小写不同的重复 property 名

验收点：

- `Completion.Abstractions` 内不再出现“新类型依赖旧类型完成合法性校验”的反向关系
- schema 边界已经消除 case-only property duplication 这类潜在二义性

### 阶段 B：先把 authoritative metadata 切到 `ToolDefinition`

目标：

- `ITool` 提供完整 `ToolDefinition`
- `ToolDefinitionBuilder.FromTool(...)` 删除或退化为一次性迁移适配
- `MethodToolWrapper` 直接生成 `ToolDefinition`
- 所有 metadata override path 改为基于 `ToolDefinition` 工作

关键设计：

- `ToolExecutor` 不再从 `tool.Parameters` 反向重建 `ToolDefinition`
- `ITool.Name` / `Description` 若保留，只能从 `Definition` 派生
- `TextAdv` 一类 override 场景必须明确改为“重写整个 `ToolDefinition`”或“受限 transform”
- `MethodToolWrapper` 第一阶段仍可只支持 flat 方法签名，但其产物必须直接是 `ToolDefinition`

这里尤其要点名一条现有高风险链路：

- `PlayerActionGuideCatalog.PlayerToolMetadata`
- `LlmPlayerAgentDriver.ToolMetadataOverrideTool`

这条链路当前会主动覆写 tool 的 `Name` / `Description` / `Parameters`，本质上是第二元数据源。进入目标态后，必须收口成：

- 直接提供完整替换后的 `ToolDefinition`
- 或定义一个只读 transform，由系统从 inner tool 的 `Definition` 派生出新 `Definition`

验收点：

- tool registration 主链上的 schema 真源已经变成 `ITool.Definition`
- 不再存在 `ToolParamSpec -> ToolDefinition -> InputSchema` 这种“旧真源藏深一层”的主链

### 阶段 C：把执行解析器改成 schema-driven

目标：

- 将 `JsonArgumentParser.ParseArguments(...)` 的输入改为 `ToolSchema.Object`
- 让执行侧按 `ToolSchema` 现有约束做运行时校验，而不只做类型转换
- 在 unknown property / 大小写匹配上与 schema 语义达成一致
- 新增 object / array 递归解析测试

关键设计：

- object 节点负责 `required` 与 `additionalProperties`
- 若 schema 为 `additionalProperties = false`，unknown property 直接作为 parse error，而不是沿用旧的 `warning + 透传`
- 属性名匹配策略应与声明语义统一；建议按 `Ordinal` 精确匹配，必要时额外给出 casing warning
- object/array 的运行时物化形状固定为本计划在目标态中规定的 dictionary/list 约定
- value 节点负责 scalar parse / nullable / `stringEnumValues` / `minLength` / `maxLength` / `pattern` / `minimum` / `maximum` 校验
- default 是否物化进 arguments 可继续由 binder / tool wrapper 决定，不必强行塞进 parser
- error key 改为 path-aware，例如 `filters.tags[1]`

这里明确接受一个有意为之的行为变化：

- 对于由 `CreateFlat(...)` 迁来的旧工具，unknown args 的执行期行为会从“warning + 透传”收紧为“按 schema 拒绝”

原因是当前 provider schema 已经声明 `additionalProperties = false`，运行时继续透传只会让“声明”和“执行”保持分叉。

验收点：

- `ToolExecutor` 不再依赖 `ToolParamSpec`
- `ToolSchema.Value` 上已有的 `stringEnumValues` / `minLength` / `maxLength` / `pattern` / `minimum` / `maximum` 在执行侧具备一致校验
- tool execution 对 unknown property / property casing 的处理与 schema 声明不再冲突

### 阶段 D：删除旧 schema 入口

目标：

- 删除 `ToolDefinition.Parameters`
- 删除 `ToolDefinition.CreateFlat(...)`
- 删除 `ToolParamSpec`

前置条件：

- 所有生产/原型调用点都已迁到 `ToolDefinition` / `ToolSchema`
- 展示层若还需要 flat 视图，应由只读 adapter 在边缘即时生成，而不是回流成核心模型

验收点：

- 代码库中不再存在 `ToolParamSpec` 的主链依赖

---

## 8. 测试与回归面

至少应覆盖：

- `ToolSchema.Value` 默认值与约束校验
- flat schema 在新 parser 下保留既有标量 coercion 行为，但 unknown args 按新 schema 语义收紧
- optional without default 在 schema-driven parser 下可正确表达
- nested object / array 的 success / warning / error
- nested object / array 的运行时物化形状符合 dictionary/list 约定
- unknown property 与 `additionalProperties` 的行为
- provider projection 不受执行侧迁移影响
- `MethodToolWrapper` 生成的定义仍能被 `ToolExecutor` 执行

特别值得补的一条是：

- “声明 builder -> provider projection -> executor parse”的串联测试

这条测试能防止未来再次出现“声明侧和执行侧各自演化”的分叉。

---

## 9. 风险判断

### 9.1 最大风险：把范围做得过大

最需要避免的是把这些事一次性绑死：

- schema 真源统一
- parser 重写
- CLR 复杂对象绑定
- 文档全面重写

建议严格按阶段推进，不把“最终理想形态”强塞进第一包。

### 9.2 次大风险：留下新的隐性双真源

例如：

- `ITool.Definition.InputSchema` 是一套
- `ITool.Parameters` 又缓存另一套
- 两边各自可编辑

这会比现在更糟，因为分裂会被藏起来。

所以迁移期间如果要兼容：

- 只能从 schema 单向投影出只读兼容视图
- 不能允许两边都被当成可写真源

### 9.3 需要接受的现实限制

即便完成本计划，短期内仍可能存在：

- 一些原型代码只消费 flat 展示信息
- nullable provider schema 语义仍待单独收口
- `ReflectedToolDefinitionBuilder` 仍不支持 nullable object/array 与嵌套 collection

这不构成阻塞，只要它们不再决定核心 schema 设计即可。

---

## 10. 建议的执行顺序

推荐工作包顺序：

1. `Completion.Abstractions`：切断 `ToolSchema` 对 `ToolParamSpec` 的反向依赖
2. `Completion.Tools`：先把 `ITool` / `MethodToolWrapper` / metadata override 链迁到 authoritative `ToolDefinition`
3. `Completion.Tools`：再把 argument parser 与 executor 改成 schema-driven
4. 文档与调用点：迁移 quick-start、测试、原型示例
5. 最后删除 `ToolParamSpec` 及其兼容入口

这个顺序的原因是：

- schema 真源先收口
- authoritative metadata 先切换，避免旧真源藏在适配层后面
- 执行主链再迁移
- 入口与示例最后一起清尾

这样最不容易出现“新文档写了，但运行时还是旧世界”的半收口状态。

---

## 11. 最终建议

这次 refactor 值得做，而且应该尽快做；因为现在已经进入一个尴尬阶段：

- 新世界的 `ToolSchema` 已经能表达正确模型
- 旧世界的 `ToolParamSpec` 还在执行边界占坑
- 再拖下去，只会让更多新代码不知道该站哪一边

但实施时要保持克制：

- 先统一 schema 真源
- 再迁执行链
- 最后删除旧类型

只要按这个节奏推进，我认为这次收口既不会变成无底洞，也能把 Completion 这套 tool declaration 的长期设计真正拉直。
