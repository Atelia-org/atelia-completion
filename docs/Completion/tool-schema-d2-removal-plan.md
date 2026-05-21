# 历史归档：Completion ToolSchema D2 清退计划（已完成）

> **归档说明**：这份文档保留的是 D2 兼容层拆除时的工作包设计，不是现行待办。
> **截至 2026-05-21 的现状**：D2 所描述的公共 API 清退已完成，`ITool` 只剩 `Definition`，`ToolDefinition` 只剩 `Name / Description / InputSchema`，schema 真源只剩 `ToolSchema`。
> **阅读提示**：下文提到的 `ITool.Name` / `Description` / `Parameters`、`ToolParamSpec`、`CreateFlat(...)` 等，都是当时待删除对象的历史记录。
> **原始目标**：在当前 A / B / C / D1 已落地的基础上，进入下一阶段 D2：真正删除 `ITool.Name` / `Description` / `Parameters` 这些旧表面，并清理仍依赖 `ToolParamSpec` / `ToolDefinition.CreateFlat(...)` 的剩余示例、测试和文档。
> **前情文档**：[tool-schema-unification-refactor-plan.md](/repos/focus/atelia/docs/Completion/tool-schema-unification-refactor-plan.md)
> **最后更新**：2026-05-21

---

## 1. 已完成状态

本计划记录的 D2 清退工作已经完成。当前代码的最终状态是：

- `ToolDefinition.InputSchema` 已是声明真源
- `ITool.Definition` 已是工具元数据真源
- `ToolExecutor` / `JsonArgumentParser` 已切到 schema-driven 执行
- `AgentEngine` 注册/查重/移除已统一按 `Definition.Name`
- `ToolParamSpec` / `ToolDefinition.CreateFlat(...)` / `ToolDefinition.Parameters` 已删除
- 文档指南类文件已切到显式 `ToolSchema` 口径

下文保留的是 D2 立项时为何要拆、当时怎么拆的过程记录。

---

## 2. 立项时的真实残留面（历史快照）

### 2.1 仍未删除的旧公共表面

当前核心残留还在这里：

- [ITool.cs](/repos/focus/atelia/prototypes/Completion.Tools/ITool.cs)
  还保留 `Name` / `Description` / `Parameters`
- [ToolDefinition.cs](/repos/focus/atelia/prototypes/Completion.Abstractions/ToolDefinition.cs)
  还保留 `Parameters` 兼容投影和 `CreateFlat(...)`
- 同文件中的 `ToolParamSpec`
  仍是公共类型

这三者就是 D2 真正要拆掉的兼容层。

### 2.2 仍在生产代码里使用旧 flat 入口的地方

当前生产/原型代码里，真正还在显式构造 flat schema 的地方主要有：

- [MethodToolWrapper.Impl.cs](/repos/focus/atelia/prototypes/Completion.Tools/MethodToolWrapper.Impl.cs)
  仍先构 `ToolParamSpec[]`，再走 `ToolDefinition.CreateFlat(...)`
- [PlayerActionGuideCatalog.cs](/repos/focus/atelia/prototypes/TextAdv/PlayerActionGuideCatalog.cs)
  四个玩家工具 metadata 仍用 `CreateFlat(...)` + `ToolParamSpec`
- [GameActionValidator.cs](/repos/focus/atelia/prototypes/TextAdv/GameActionValidator.cs)
  仍用 `CreateFlat(...)`
- [DeepSeekDebug/Program.cs](/repos/focus/atelia/prototypes/DeepSeekDebug/Program.cs)
  仍用 `CreateFlat(...)`

这些不是单纯“测试债”，而是 D2 真要迁掉的生产侧调用点。

### 2.3 仍在消费 `Parameters` 兼容投影的地方

当前 `.Parameters` 的残留用途，已经明显收缩到“展示/断言/测试替身”：

- [LlmPlayerAgentDriver.cs](/repos/focus/atelia/prototypes/TextAdv/LlmPlayerAgentDriver.cs)
  `BuildInitialObservation(...)` 仍按 `tool.Parameters` 渲染 prompt
- [MethodToolWrapper.cs](/repos/focus/atelia/prototypes/Completion.Tools/MethodToolWrapper.cs)
  仍把 `Definition.Parameters` 暴露为兼容属性
- [ToolContracts.cs](/repos/focus/atelia/prototypes/Completion.Tools/ToolContracts.cs)
  `EnsureStableFlatProjection(...)` 仍依赖 `definition.Parameters`
- 多个测试 double / 测试断言
  仍把 `Definition.Parameters` 当作可见公共 API

这说明 D2 不是“直接删掉字段就完事”，而是必须先给展示与测试找到替代路径。

### 2.4 文档仍有明显 flat 心智残留

当前最值得一并收口的文档主要是：

- [quick-start.md](/repos/focus/atelia/docs/Completion/quick-start.md)
  仍把 `ToolDefinition` / `ToolParamSpec` 作为主要上手入口之一
- [memory-notebook.md](/repos/focus/atelia/docs/Completion/memory-notebook.md)
  仍明确记载 `CreateFlat(...)` / `ToolParamSpec` 是过渡入口
- [structured-tool-schema-plan.md](/repos/focus/atelia/docs/Completion/structured-tool-schema-plan.md)
  还停留在“阶段 1 已落地”语境，适合作为历史文档而不是现行方案

---

## 3. D2 的目标态

### 3.1 核心类型目标态

完成 D2 后，目标应是：

- `ITool` 只保留：
  - `ToolDefinition Definition`
  - `bool Visible`
  - `ExecuteAsync(...)`
- `ToolDefinition` 只保留：
  - `Name`
  - `Description`
  - `InputSchema`
- `ToolParamSpec` 删除
- `ToolDefinition.CreateFlat(...)` 删除
- `ToolDefinition.Parameters` 删除

换句话说，D2 结束后，外部可见 schema 类型只剩 `ToolSchema` 系列。

### 3.2 展示与提示的替代方案

D2 不能再靠 `Definition.Parameters` 给 prompt / 调试输出做平铺展示，因此需要一个替代路径。

推荐方案是：

- 新增一个**只读 schema 展示 renderer**
- 输入 `ToolDefinition` 或 `ToolSchema.Object`
- 输出适合 prompt/debug 的文本视图

这个 renderer 是：

- 单向读取
- 非 schema 真源
- 不回流成新的数据模型

它不是新的兼容层，而是 D2 删除 `Parameters` 后的展示适配器。

我建议它放在 Completion 层内部，而不是继续让 `TextAdv` 手写一份只适合 flat 参数的渲染逻辑。

### 3.3 flat 方法包装的目标态

`MethodToolWrapper` 当前仍是 flat CLR 签名包装器，但这不要求继续保留 `ToolParamSpec`。

完成 D2 后，它应当：

- 直接从方法签名构造 `ToolSchema.Property[]`
- 直接产出 `new ToolDefinition(..., inputSchema: new ToolSchema.Object(...))`
- `ArgGetter` 继续只处理 top-level flat 参数名即可

也就是说：

- “仍只支持 flat CLR 方法签名”可以保留
- “内部必须靠 `ToolParamSpec` 过桥”应该删除

---

## 4. 核心设计决策

### 4.1 不再新增新的 public flat helper

D2 不建议新增诸如：

- `ToolDefinition.CreateFlatValueOnly(...)`
- `ToolParamSpecLite`
- `FlatToolDefinitionBuilder`

之类的新公共 helper。

原因很简单：

- 这会把“即将删除的兼容入口”重新包装成新的兼容入口
- 只会延迟真正的清退

若需要减少样板代码，应优先：

- 在测试文件内部写局部 helper
- 在 `MethodToolWrapper` 等内部实现里直接生成 schema

### 4.2 展示 renderer 可以新增，但必须是只读边缘件

D2 适合新增的 helper 只有一种：

- **schema 文本渲染器**

它的职责只应是：

- 把 `ToolSchema` 渲染成 prompt/debug 可读文本

不应负责：

- 参数绑定
- schema 比对
- 反向生成 `ToolParamSpec`

这样才能确保它不会再次演化成兼容真源。

### 4.3 `ToolContracts.EnsureStableFlatProjection(...)` 应一并退出主链

当前这个 helper 的存在，是因为 B 阶段曾经需要 flat-only 护栏。

但现在 C 已经把执行解析切成 schema-driven，所以 D2 时它不应再继续扮演主链守门员。

推荐处理：

- 删除主链对 `EnsureStableFlatProjection(...)` 的依赖
- 若某些测试还要验证“某 schema 是否能无损平铺展示”，可改成测试专用 helper 或新的 display renderer 行为断言

### 4.4 `CreateCompatibleFlatOverride(...)` 应改名

当前名字已经不符合真实语义了。

它现在做的是：

- 校验 override 是否只修改 metadata、未改 provider-visible schema

而不是：

- “compatible flat override”

所以 D2 里建议顺手把它改名为更贴近语义的名字，例如：

- `CreateMetadataOnlyOverride(...)`
- `ValidateMetadataOnlyOverride(...)`

这是低成本但高收益的收口。

---

## 5. 建议的工作包拆分

### 工作包 D2-A：删除 `ITool` 旧表面依赖

目标：

- 把所有主链/测试/替身从 `tool.Name` / `tool.Description` / `tool.Parameters` 迁到 `tool.Definition`
- 删除 `ITool` 上这三个属性

写入重点：

- [ITool.cs](/repos/focus/atelia/prototypes/Completion.Tools/ITool.cs)
- [MethodToolWrapper.cs](/repos/focus/atelia/prototypes/Completion.Tools/MethodToolWrapper.cs)
- [LlmPlayerAgentDriver.cs](/repos/focus/atelia/prototypes/TextAdv/LlmPlayerAgentDriver.cs)
- Agent.Core / LiveContextProto 相关测试替身

关键前置：

- 先引入 schema 文本 renderer，替换 `LlmPlayerAgentDriver` 对 `.Parameters` 的 prompt 渲染

完成定义：

- 代码库中不再存在对 `ITool.Name` / `Description` / `Parameters` 的主链依赖
- `ITool` 接口层面正式删除这三个属性

### 工作包 D2-B：删除 `CreateFlat(...)` 与 `ToolParamSpec` 的生产侧使用

目标：

- 让所有生产/原型代码不再显式构造 `ToolParamSpec`
- 让所有生产/原型代码不再调用 `ToolDefinition.CreateFlat(...)`

写入重点：

- [MethodToolWrapper.Impl.cs](/repos/focus/atelia/prototypes/Completion.Tools/MethodToolWrapper.Impl.cs)
- [PlayerActionGuideCatalog.cs](/repos/focus/atelia/prototypes/TextAdv/PlayerActionGuideCatalog.cs)
- [GameActionValidator.cs](/repos/focus/atelia/prototypes/TextAdv/GameActionValidator.cs)
- [DeepSeekDebug/Program.cs](/repos/focus/atelia/prototypes/DeepSeekDebug/Program.cs)

关键决策：

- `MethodToolWrapper` 直接构造 `ToolSchema.Object`
- TextAdv / 示例代码改写为显式 `ToolDefinition + ToolSchema`

完成定义：

- 生产/原型代码中不再出现 `new ToolParamSpec(...)`
- 生产/原型代码中不再出现 `ToolDefinition.CreateFlat(...)`

### 工作包 D2-C：删除 `ToolDefinition.Parameters` 与 `ToolParamSpec`

目标：

- 删掉兼容投影与旧类型本体

写入重点：

- [ToolDefinition.cs](/repos/focus/atelia/prototypes/Completion.Abstractions/ToolDefinition.cs)
- 与之直接耦合的 tests
- [ToolContracts.cs](/repos/focus/atelia/prototypes/Completion.Tools/ToolContracts.cs)

需要一起删除/重写的内容：

- `Parameters`
- `CreateFlat(...)`
- `NormalizeParameters(...)`
- `BuildFlatInputSchema(...)`
- `TryProjectFlatParameters(...)`
- `ToolParamSpec`

完成定义：

- `Completion.Abstractions` 对外不再暴露 `ToolParamSpec`
- `ToolDefinition` 不再承担 flat projection 兼容职责

### 工作包 D2-D：测试与文档总清尾

目标：

- 清理剩余测试、示例、文档中的 flat 心智

重点文件：

- `tests/Completion.Tests/*`
- `tests/Atelia.LiveContextProto.Tests/*`
- [quick-start.md](/repos/focus/atelia/docs/Completion/quick-start.md)
- [memory-notebook.md](/repos/focus/atelia/docs/Completion/memory-notebook.md)
- [structured-tool-schema-plan.md](/repos/focus/atelia/docs/Completion/structured-tool-schema-plan.md)

建议做法：

- `structured-tool-schema-plan.md` 改成历史归档口吻，明确它不是现行实施指南
- `quick-start.md` 的工具声明示例改成显式 `ToolSchema`
- 测试中若只是需要简单 flat schema，可用测试内部 helper，而不是保留公共 flat API

---

## 6. 推荐顺序

推荐顺序是：

1. D2-A：先移除 `ITool` 旧表面依赖
2. D2-B：再清生产侧 `CreateFlat(...)` / `ToolParamSpec`
3. D2-C：最后删 `ToolDefinition.Parameters` / `ToolParamSpec` / `CreateFlat(...)`
4. D2-D：补完测试与文档清尾

原因是：

- 先把调用方从旧表面挪开
- 再删底层兼容入口
- 最后统一清测试和文档

这比“先删 core API，再到处修编译”更稳，也更适合多人/多 agent 并行。

---

## 7. 测试策略

D2 至少应覆盖下面这些断言：

- `ITool` 主链不再依赖 `Name` / `Description` / `Parameters`
- `MethodToolWrapper` 不再通过 `ToolParamSpec` 过桥
- `TextAdv` 的 prompt 展示在删除 `Parameters` 后仍正常工作
- `CreateCompatibleFlatOverride(...)` 改名后，override 仍只允许 metadata 变化
- 删除 `ToolDefinition.Parameters` 后，schema-driven 执行主链行为不回退
- 生产/原型代码不再调用 `CreateFlat(...)`
- 文档示例中的工具声明与当前 API 一致

我建议把测试分成两层：

- 编译期迁移测试：确认旧表面删除后所有调用点都更新了
- 行为测试：确认展示、注册、执行主链仍然成立

---

## 8. 风险与边界

### 8.1 最大风险：把“删除兼容层”和“增强复杂绑定”混到一起

D2 不应该顺手做：

- 嵌套 CLR object binder
- `ToolParamType` 重命名
- provider schema 新能力扩张

这些会稀释目标，让“删旧表面”变成另一个大重构。

### 8.2 次大风险：展示层临时回退成新的 flat 兼容模型

例如：

- 新造一个 `ToolDisplayParam`
- 新造一个 `PromptParamSpec`
- 再从 schema 投影出一套长期存在的新平铺模型

这会让 D2 表面上删掉了 `ToolParamSpec`，实则又造出一个替身。

所以展示层只能新增：

- 一次性渲染器

不能新增：

- 新的中间参数模型

### 8.3 需要接受的现实

D2 完成后，仍可能暂时保留：

- `MethodToolWrapper` 只支持 flat CLR 方法签名
- 某些旧设计文档以历史资料形式存在

这不构成阻塞，只要它们不再影响主链 API 与运行时语义即可。

---

## 9. 最终建议

D2 应该被当作“兼容层拆除工程”来做，而不是“再发明一个更漂亮的过渡层”。

最值得坚持的三条原则是：

- 先迁调用方，再删底层兼容 API
- 展示用 renderer，不用新平铺模型
- 生产代码先清，测试/文档随后统一扫尾

如果按这个节奏推进，D2 结束时，Completion 这套工具模型就会真正只剩一条主线：

- `ITool.Definition`
- `ToolDefinition.InputSchema`
- `ToolSchema`

到那一步，`ToolParamSpec` 才算真正退出历史舞台。
