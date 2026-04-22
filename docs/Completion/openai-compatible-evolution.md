# OpenAI-Compatible 演进指南

> **用途**：指导后续 LLM Agent 会话在 `OpenAI Chat Completions` 或更广义 `OpenAI-compatible` 端点遇到不兼容问题时，如何增量分析、设计与落地。
> **适用范围**：`prototypes/Completion/OpenAI/*`
> **最后更新**：2026-04-23

---

## 一句话原则

**Strict Core + Quirk Modules**

- 保持一套尽量保守、尽量接近官方语义的严格内核
- 只把已经真实撞到的不兼容点抽成小颗粒度 quirk
- 端点差异通过组合这些 quirk 解决，而不是堆布尔 flag 或按厂商名写分支森林

---

## 目标

这套方法要同时满足四件事：

1. **默认路径清晰**
   - 主路径始终是“最保守、最容易解释”的实现。
2. **增量扩展自然**
   - 新兼容问题优先以“增加新代码”的方式解决，而不是改脏旧逻辑。
3. **差异局部化**
   - 某个端点的特殊处理应局限在少量策略点中，不污染整条调用链。
4. **测试可复用**
   - 测试优先验证行为契约，而不是绑定某个厂商名字。

---

## 不推荐的路径

### 1. 端点名驱动分支

避免：

```csharp
if (isSgLang) { ... }
if (isLiteLlm) { ... }
if (isVllm) { ... }
```

问题：

- 差异被“厂商名”掩盖，真实行为约束不清楚
- 同一端点不同版本可能差异不同
- 相同行为差异会在多个端点分支中重复出现

### 2. 全局 flag 爆炸

避免：

```csharp
new OpenAIClient(
    allowLooseToolOrder: true,
    retryOnMissingUsage: true,
    ignoreReasoningContent: true,
    tolerateBrokenDoneFrame: true,
    ...
)
```

问题：

- 调用点难以理解组合语义
- 新增 flag 很容易影响已有路径
- 测试矩阵会迅速膨胀

---

## 推荐演进方式

### 1. 先维护 Canonical Contract

内部抽象层继续保持稳定：

- `CompletionRequest`
- `IHistoryMessage`
- `ParsedToolCall`
- `CompletionChunk`

这层不承载某个兼容端点的特殊细节。

### 2. 把已知差异收敛为 Dialect

`Dialect` 不是“完整 profile 系统”，而是当前已确认存在的少数行为差异的组合。

当前最值得抽出来的差异点：

- **Tool result projection**
  - `ToolResultsMessage` 如何映射为 OpenAI `messages[]`
- **Stream usage mode**
  - 是否请求 `stream_options.include_usage`
  - 若端点拒绝该字段，是否自动降级重试
- **Whitespace content noise during tool streaming**
  - 是否忽略工具调用过程中夹带的纯空白 `delta.content`

未来只有当真实问题反复出现时，才继续增加新的差异维度。

### 3. 第 3 次再抽策略对象

实用规则：

- 第 1 次遇到某类差异：局部修复
- 第 2 次遇到同类差异：确认这是一个稳定变化点
- 第 3 次遇到同类差异：从 enum / switch 提升为独立策略对象

换句话说：

- **先窄枚举**
- **后策略对象**

这样既能避免过度设计，也能避免后期被 `if` 淹没。

---

## 处理不兼容问题的标准流程

### Step 1. 先确认是“请求侧”还是“响应侧”问题

先回答两个问题：

1. 端点是拒绝了**我们发出去的 payload**？
2. 还是接受请求后，返回的 **stream / JSON 方言** 和预期不同？

分类后再设计，不要把请求兼容和响应兼容混在一起改。

### Step 2. 优先抽象为“行为差异”，不要抽象为“厂商差异”

优先问：

- 是不是“不支持 `stream_options.include_usage`”？
- 是不是“要求 tool 消息必须紧邻 assistant tool_calls”？
- 是不是“`finish_reason` 语义不同”？

而不是先问：

- “这是 sglang 还是 vllm？”

### Step 3. 先选最保守写法

请求侧原则：

- **写出严格**
- **读入宽容**

也就是：

- 发请求时尽量按最保守的 OpenAI 语义组织 payload
- 读响应时允许更多兼容变体，并补好诊断日志

### Step 4. 评估差异是否值得进入 Dialect

只有满足以下任一条件，才应新增一个 dialect 维度：

- 已经出现第 2 个真实端点需要同类处理
- 同一类分支开始出现在多个文件里
- 不抽出来会让测试和调用点都越来越难读

否则就先在局部修复，并留下文档/测试样本。

### Step 5. 固化为测试

每次兼容修复都至少补一类测试：

- 请求 payload 快照 / 结构断言
- stream 解析断言
- 错误回退断言

优先测“行为契约”，例如：

- tool 结果必须连续出现在 assistant tool_calls 之后
- 请求 usage 时，端点若报 `stream_options` 不支持，则自动重试去掉字段
- 非 2xx 错误必须带上响应 body

---

## 当前已确认的高价值差异点

### 1. ToolResults 投影顺序

OpenAI Chat Completions 语义下，`role="tool"` 消息必须与上一条 `assistant.tool_calls` 紧密对应。

因此当前默认采用：

1. 先输出所有 `role="tool"` 消息
2. 再把额外观测和执行错误合并为 trailing `user` 消息

不要把 `ToolResultsMessage.Content` 插到 `tool` 消息前面。

### 2. Stream usage 支持性不一致

不同兼容端点对 `stream_options.include_usage` 的支持程度不同。

因此需要把 usage 请求语义视为一个独立变化点，而不是写死在 client 主流程里。

### 3. 工具流中的纯空白 `content` 噪声

已观察到部分兼容端点会在 `tool_calls` 增量之间插入仅含空白的 `delta.content`（典型是单个换行）。

- `Strict` 路径应保留这类内容，避免隐式改写 transcript
- 只有明确确认存在该噪声的 dialect，才应在“已有 pending tool calls 且 content 全空白”时丢弃

---

## 推荐的代码落点

当前建议把差异收敛在 OpenAI 层内部：

- `OpenAIChatDialect`
- `OpenAIChatToolResultProjectionStyle`
- `OpenAIChatStreamUsageMode`

不要把这些差异泄漏到 `Completion.Abstractions`。

---

## 何时升级到更强的组件化

只有当以下情况出现时，才考虑从轻量 dialect 升级到真正策略对象：

- tool result 有 3 种以上稳定投影方式
- usage / retry / fallback 出现 3 种以上稳定行为
- 某个 switch 开始同时影响 2 个以上核心类

到那时再把对应枚举升级为：

- `IToolResultProjectionPolicy`
- `IStreamUsagePolicy`
- `IHttpFailurePolicy`

在那之前，保持轻量即可。

---

## 给后续 Agent 的实施约束

后续会话若要修 OpenAI-compatible 问题，请遵守：

1. **先记录真实端点症状**
   - 请求 payload、状态码、响应 body、SSE 样本
2. **先判断是否属于现有 dialect 维度**
   - 若属于，优先复用当前 dialect 机制
3. **若不属于，先局部修复，再决定是否新增维度**
4. **不要直接引入厂商名分支**
5. **不要先加一堆预防性 flag**
6. **每次兼容修复都要补测试和文档样本**

---

## 当前建议的节奏

就 Atelia 当前阶段而言，最推荐的策略是：

- 默认保持轻量 `Dialect`
- 遇到真实兼容问题时再扩展
- 优先提升可诊断性，其次才是自动容错

这样可以在不做大规模兼容调研的前提下，长期稳步演进。
