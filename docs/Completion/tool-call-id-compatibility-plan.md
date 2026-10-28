# Tool-call ID 兼容性与消息来源：工作计划及续接入口

更新日期：2026-10-28。代码调查基线：`9dcc9f3d69dfe87b085eca8cae2d81bc1736610c`。

本文记录用户目标、已知证据、待决策项和阶段验收，供上下文压缩或新会话后继续工作。
它不是已实现 API 的说明，也不表示已完成在线兼容性验收。继续工作时以当前源码和实际测试为准。

## 当前状态与下一步

| 阶段 | 状态 | 下一交付 |
| --- | --- | --- |
| 1. 历史消息承载来源规格 | 暂缓；尚未改 API | 仅在实测需要时继续设计 |
| 2. 端点间 ID 兼容性实验 | runner、Gemini 兼容修复与 4×4 全矩阵已完成 | 用户追加端点/model 后重跑 |
| 3. 兼容性映射 | 暂缓；无生产实现 | 有实测反例后再决定是否需要 |

**用户已调整顺序：先做阶段 2 的兼容性矩阵。** 如果主流端点实际兼容良好，可以不实现映射层，
也不因本次实验预先改造 `ActionMessage`。阶段 1 的来源承载候选保留供有需求时评估。

已交付 [runner 使用说明](../../experiments/ToolCallIdMatrix/USAGE.md) 与
[2026-10-28 实测报告](experiments/2026-10-28-tool-call-id-matrix.md)。两轮合计 33 次模型 POST，
补测四个来源到三个可测目标的 12 格全部通过。之后按用户授权在实验分支修改 Gemini adapter，
解决本地原生 replay 要求过严，并完成 [Gemini 兼容修复与 4×4 全矩阵](experiments/2026-10-28-gemini-history-compatibility.md)。
新增 40 次模型 POST 全部 Completed，16 格 ID 矩阵全通过，异源实际消息的当前/下一回合续接通过。
原有转发站 Chat 路由 404 尚未补测。当前证据支持继续暂缓阶段 1/3。
修复提交 `e2cfab7`；完整离线 1,198 passed/3 平台 skipped，最终 Gemini 165 passed，runner 自检 24 项通过。
生产 Gemini 实现已修改，公共 API 与 ActionMessage 未改；分支、本地合并及证据详见上述报告。

当前按 2 → 实测裁决 → 必要时 1/3 推进。局部源码调查、实现和结果复核可以委派；
不将尚未证明必要的来源绑定和映射层分叉实现。
若用户之后选择 fork，应共享本文与已确定的合同，明确各分支写入范围，由主线程整合验收。

每阶段结束更新本文的状态、实际交付位置、验证结果及下一个动作。
实验的原始证据与详细报告另存，本文只保留摘要和链接，不把计划改写成完成记录。

## 用户目标与明确边界

- ATELIA 面向长期生存和活动的 Agent，不以一次性会话为目标。
- 不因保存调用来源而要求整个 tool-loop 固定端点或模型。模型退役、端点不可用时，
  历史来源仍应如实保留，宿主可以选择新的目标端点继续工作。
- 来源描述是历史事实，不是调用路由指令，也不是当前可用连接或凭据。
- 不承诺任意原生 reasoning/opaque payload 均可跨协议回放；端点切换能力与这些协议限制分开处理。
- 常见合法 ID 尽量原样保留。只有实际格式不兼容时才引入稳定转换，不统一重编号全部历史。
- 兼容工作由 Completion 层承担，避免宿主自行实现各家 ID 格式修复。
- 实验需要记录真实生成 ID 的完整文本及 source → target 两个方向的结果。
- 一般端点通过 Client 类型、BASE_URL、模型和 API_KEY 环境变量名配置；
  Codex ChatGPT 订阅使用现有 `OpenAICodexResponsesClient` 和本地凭据读取机制。
- 不引入外部工具副作用、库内重试、持久化事务或 exactly-once 承诺；不改变包依赖及 TFM 边界。
- 提交、推送和发布不是本计划的自动步骤；范围仍以用户授权为准。

## 当前源码事实

| 位置 | 已核对行为与后续影响 |
| --- | --- |
| [CompletionDescriptor](../../src/Completion.Abstractions/CompletionDescriptor.cs) | 不可变 ProviderId / ApiSpecId / Model；可由 client 与 request 派生。不要与承载能力配置的 `CompletionModelSpec` 混淆。 |
| [CompletionResult](../../src/Completion.Abstractions/CompletionResult.cs) | `Invocation` 保存来源，`Message` 是纯消息体；结果 envelope 本身不是 `IHistoryMessage`。 |
| [ActionMessage](../../src/Completion.Abstractions/IHistoryMessage.cs) | 当前只有有序 Blocks，无 invocation metadata；注释明确允许上层把细粒度历史投影、合并为消息。 |
| [RawToolCall / ToolResult](../../src/Completion.Abstractions/RawToolCall.cs) | 保存 ID，但没有来源或另存原始 wire ID；构造器拒绝 null，仍允许空白。 |
| [ActionMessageSerialization](../../src/Completion.Abstractions/ActionMessageSerialization.cs) | 当前以 block 数组序列化；普通 tool-call surrogate 不含来源。增加消息属性不会自动进入这套格式。 |
| [OpenAIChatMessageConverter](../../src/Completion/OpenAI/OpenAIChatMessageConverter.cs) | 非空 ID 原样投影；空调用 ID 局部补成 `openai-call-{name}-{index}`，结果端仍拒绝空 ID，因此不是完整补齐方案。 |
| [AnthropicMessageConverter](../../src/Completion/Anthropic/AnthropicMessageConverter.cs) | call/result ID 原样投影，按 ID 配对并核对工具名。 |
| [CompletionConnections](../../src/Completion/CompletionConnections.cs) | 已有 V2 文件、V3 catalog、kind/model/surface/base address/API key 环境变量及 factory；实验配置先评估复用，不另造第二套连接语义。 |
| [Codex client](../../src/Completion/OpenAI/OpenAICodexResponsesClient.cs) | 独立订阅协议与 credential provider；不把订阅认证当普通 API key，不绕道 CLI 进程或代理。 |

上层宿主可能另存了来源，但不能假设当前只传回 `result.Message` 就足以让 converter 获取来源。
来源标识是否足以区分同协议、同模型名的不同 relay，也需核对实际 client identity，不能用秘密凭据作为规格键。

## 阶段 1：为历史 Action 绑定来源

### 候选与倾向

优先评估在 `ActionMessage` 上引用不可变来源描述符，或用同等轻量的消息 envelope 承载。
不为节省几个重复字段引入强制 tool-loop-turn 锁；turn 级去重只能是宿主选择，不能成为跨端点续接的限制。

CLR 引用与持久化引用身份是两件事。可以在内存中共享同一个描述符实例，保存时重复写入三个字符串，
加载后按值恢复；无需要求对象图序列化或引用相等。需要节省空间的宿主可以自行字典去重。
规格中不保存密钥、access token、HttpClient 或可变连接对象。

### 必须裁决的问题

1. 单个 Action 是否保证来自一次 invocation？若上层会合并不同来源的 Action，
   是禁止这种合并、保留消息边界，还是需要更细粒度来源？不要用最后一个来源覆盖整条混合消息。
2. 手工 fixture、人工构造历史、旧数据的来源缺失如何表示？未知来源不能伪装成目标端点。
3. `CompletionResult.Invocation`、Action 来源及 reasoning block 的 `Origin` 如何一致？
   创建、复制、过滤、拆分时谁写入，矛盾时如何处理，避免多份字段互相猜测。
4. 序列化要覆盖消息来源；当前 block 数组格式是否扩展或另设消息 envelope，旧格式是否继续读取，
   必须显式选择。不要顺带重写宿主持久化框架。
5. `ToolResultsMessage` 是否可从待匹配调用取得来源，避免复制第二份易漂移的来源信息。
6. 如果阶段 3 要保留原始 ID，来源载体应能扩展，但阶段 1 不提前引入未证实的全套映射状态。

### 验收

- A 产生的历史与 B 产生的历史共存，当前目标切换为 C 时，各历史来源仍真实不变。
- 保存/加载、复制及既有投影过程中来源不静默丢失；未知来源与矛盾来源有明确行为。
- 不要求 A 仍在线，不把来源当端点锁；也不放宽现有 opaque replay 的协议校验。
- 运行受影响的离线测试，记录真实覆盖；来源承载切片不附带生产 ID 转换。

## 阶段 2：双向兼容性实验

### 配置与框架交付

已交付独立 runner 与严格 JSON 配置；实际 schema 和命令以
[USAGE](../../experiments/ToolCallIdMatrix/USAGE.md) 为准。以下保留需求清单，其中边界/多调用等扩展尚未实现：

- 版本、唯一连接名、客户端种类/必要的协议 surface、modelId。
- API key 路径：base address（可间接读环境变量）、API_KEY 环境变量名。
- Codex 路径：本地 auth 文件路径或现有默认定位机制，走现有 credential provider 与订阅 client。
  不把 token 复制到配置，不让探针负责 login/refresh/write-back，不增加订阅 endpoint override。
- 选择参与端点、样本数、实验 case、最大请求数、宿主取消期限、输出目录。
- 优先复用现有连接 catalog/factory；实验独有调度选项和 Codex 分支在实验宿主处理。
  不为探针需要扩大不相关的生产配置 API。
- 提供离线自检与明确的 live 执行入口。真实请求显式 opt-in；默认离线 CI 不调用任何端点。
- 凭据仅运行时读取，不进入仓库、样本、请求日志或结果报告。

实验实现为独立小型 runner，不等待阶段 1，不改变生产消息合同，
以可重复执行、明确调用账目、容易收集证据为准，不建设常驻服务。

### 实验顺序

1. **生成样本与自回放**：各端点生成同一无副作用 JSON 工具的调用，保存原始 ID。
   必须区分端点真正返回的 ID 与本仓 parser 合成的 fallback；不能把后者登记为厂商格式。
   先取得本端点原样回放基线。
2. **有向矩阵**：行是 ID 来源，列是接收端点；覆盖对角线和 A→B、B→A。
   可以按上三角枚举端点对，但每格执行两个方向，不能假设对称性。
   N 个端点、每来源一个样本时，基础计划是 N 次生成加 N² 次回放；
   后续 case 和复验另行计入调用账目，不把这一数字当总预算保证。
3. **隔离格式因素**：用一致的最小工具历史替换 ID，同时改 call/result 两端；
   固定工具名、参数、结果、消息排列和目标配置。真实原生历史续接与这种 ID 格式探针分开报告。
   不跨协议复制 reasoning；若目标必须有原生状态，报告前置条件或不适用，不归为 ID 拒绝。
4. **边界探测**：基于真实样本增加字符集、长度、前缀、大小写等变化；
   失败时与目标已接受 ID 做单变量对照。返回错误文本可留作证据，不能将自然语言解析写成生产策略。
5. **同源重命名对照**：验证端点要求的是原始 ID 本身，还是只要求格式合法且配对一致。
   这决定是否需要还原原值，不能先假设所有端点都需要逆变换。
6. **配对案例**：加入同名多调用、结果逆序提交、连续轮次；检查关联正确，不能只看字符串被接受。

### 报告与判定

每次尝试记录 run/case ID、时间、代码 commit、来源与目标描述符、原始 ID 完整文本、
实际发出的调用/结果 ID、必要的合成请求证据、HTTP 状态、结构化失败、terminal、输出校验和 usage。
规范化后的带密钥连接对象不得整体序列化。只有合成测试数据；原生 reasoning 或凭据不因记录 ID 而被一并输出。
Windows 上的证据采集实现须单独验证，不能直接假设 Linux-only exchange file sink 可用。

分别报告：未执行、配置/客户端投影拒绝、HTTP 拒绝、provider Failed、Incomplete、
terminal 前结果未知、Completed，以及 Completed 后的结果关联检查。
HTTP 200 不等于成功，提前返回的 opt-in 测试不等于在线通过；不自动重试掩盖失败。
重跑产生新的 attempt，保留原始账目。输出机器可读 JSONL 和便于阅读的 Markdown 矩阵。

有限样本接受不等于整个字符集被支持；矩阵针对具体 endpoint/protocol/model/时间。
429、认证失败、模型退役、传输中断等不能解释成 ID 格式不兼容。
格式兼容与缓存收益分开；本阶段不要求以缓存命中率作为通过条件。

### 验收

- 用户能仅编辑 JSON 和设置凭据环境变量来运行；Codex 不需要普通 API key。
- 离线检查覆盖配置、矩阵方向、call/result 同步改写、完成判定和无凭据输出。
- 在线报告包含实际发送次数、真实生成 ID、两个方向结果、失败与未执行项。
- 框架实现完成和在线矩阵完成分开标记；未提供可用连接时不声称在线验收。

## 阶段 3：按证据实现 ID 映射

候选路由以来源规格 S、目标规格 T 与显式中立格式 N 表示：`S→N`、`N→T`、`S→T`。
只声明来源表示目标 N，只声明目标表示来源 N；不能把这种省略同时解释成通配或未知。
直接匹配的 `S→T` 规则优先，否则考虑 `S→N→T`；兼容步骤透传。
这是待验证的设计骨架，不是已批准的注册表、接口或任意多跳转换图。

在具体反例出现后确定：

- 中立 ID 的接受范围；实际选定主流端点共同接受即可，不追求覆盖所有历史/未来厂商。
- 规则的匹配维度及冲突行为；不要靠字符串外形猜来源，也不要靠注册顺序随机选规则。
- 中立 ID、原始 wire ID、目标 wire ID 的信息责任；不可逆缩短需要保留原值，知道来源不能逆转哈希。
- 映射从稳定历史事实投影，不把上一次目标 wire ID 再当下一个端点的来源 ID。
- call/result 共用一次映射决策，原始历史不被目标投影改写。
- 相同历史重复投影一致，末尾追加不改变旧前缀 ID；不能预扫描未来 ID 后回改旧映射。
- 处理碰撞、重复源 ID 的作用域与未知来源；不把整个会话原生 ID 全局唯一冒充现有合同。
- 旧历史保存/加载、混合来源、多调用、同源返回及不兼容输入的验收。
- 现有签名/opaque payload 可能包含原始 ID；不能只改可见字段并假定原生 replay 仍合法。

只实现实验揭示的必要规则，运行相关离线及已授权的在线回归；不附带工具执行重试或副作用修复。

## 前期证据：防止恢复工作时丢失关键结论

### 客户端实践

- [Copilot 映射源码](https://github.com/microsoft/vscode/blob/37995fde4ddebe8bbe8b85b64f9f1334292ae523/extensions/copilot/src/platform/endpoint/node/messagesApi.ts#L40-L90)：
  合法 ID 保留，非法字符清洗，call/result 共用映射；全历史预留合法 ID 会导致后来 ID 冲突时旧映射改变。
- [OpenCode Mistral 映射](https://github.com/anomalyco/opencode/blob/fa1260d6ca484fe0fd868aa992ad81ab85343cf3/packages/ai/src/protocols/utils/mistral-tool-id.ts#L3-L31)：
  确定性 hash 与配对映射值得参考；同样不能把同完整输入确定性当作追加前缀稳定。
- [OhMyPi fallback](https://github.com/can1357/oh-my-pi/blob/c3b645cb275e03ac8a176fbfb0a877cfb3a2c2ef/packages/ai/src/providers/openai-completions.ts#L2411-L2440)：
  有位置相关的确定性补齐；但前置清洗会删除真正空白 ID 的调用，不是普遍的无 ID 历史支持。

### 模型可见性与缓存

- 官方 [Qwen3-0.6B tokenizer](https://huggingface.co/Qwen/Qwen3-0.6B/blob/c1899de289a04d12100db370d81485cdf75e47ca/tokenizer_config.json)：
  本次只成对改 ID，thinking true/false 分别 184/188 tokens，A/B prompt 与 token IDs 均完全相同。
- [vLLM Mistral 示例模板](https://github.com/vllm-project/vllm/blob/1cb31be7028457c7eae7b6fabca1941d5d3c4730/examples/tool_chat_template_mistral.jinja#L51-L82)
  会渲染末九位 ID；仓库 Hermes 模板忽略 ID。该结论不代表全部 Mistral 原生 tokenizer 版本。
- [vLLM 块哈希](https://github.com/vllm-project/vllm/blob/1cb31be7028457c7eae7b6fabca1941d5d3c4730/vllm/v1/core/kv_cache_utils.py#L637-L736)
  由前块 hash、token IDs 与额外隔离因素构成，不独立加入原始 tool-call ID。
- [SGLang GLM renderer](https://github.com/sgl-project/sglang/blob/c240169265af83800d0b901661c1075f9c5ed3ff/python/sglang/srt/parser/glm53_chat_template.py#L218-L273)
  不输出 ID 字面值，但按 ID 排列结果；实跑一致重命名不改变 prompt，破坏关联或清空 ID 可改变 prompt。
- [SGLang RadixKey](https://github.com/sgl-project/sglang/blob/c240169265af83800d0b901661c1075f9c5ed3ff/python/sglang/srt/mem_cache/radix_cache.py#L59-L82)
  不独立包含原始 ID；但其[多模态模板 CPU 缓存](https://github.com/sgl-project/sglang/blob/c240169265af83800d0b901661c1075f9c5ed3ff/python/sglang/srt/entrypoints/openai/serving_chat.py#L2271-L2297)
  使用完整 messages 作 key，ID 变化可能只使这层 miss，最终 tokens 相同仍可复用 KV。

这些是源码检查与无 GPU 的模板/tokenizer/孤立函数探针，不是在线 KV 命中率实测，
不能外推 OpenAI/Anthropic 商业后端。稳定 ID 有确定性与格式兼容价值，不必然提高所有后端 KV 命中率。

本机临时工件（可能被清理；不能作为后续构建或测试前提）：

- `W:\TEMP\tool-id-token-probe-20261028\probe.py` 与 `probe-result.json`。
- `W:\TEMP\vllm-tool-id-probe.py`、`vllm-tool-id-probe-results.json`；源码 `vllm-tool-id-inspect`。
- `W:\TEMP\sglang-tool-id-probe.py`、`sglang-tool-id-probe-result.json`；源码 `sglang-tool-id-audit-20261028`。
- `W:\TEMP\copilot-tool-id-research-20261028\probe-append-stability.cjs`。

## 续接检查单

1. 读本文状态，再读仓库 AGENTS.md、README、快速上手与传输合同，检查 git 工作区。
2. 先读实测报告；阶段 2 最小实验已完成，下一步扩充端点，不要提前实现来源绑定或映射 registry。
3. 代码变更按当前 CI 关闭 live 开关并串行执行相关 dotnet 验证；未执行在线项明确标注。
4. 用户已提供 WSL Galatea 连接配置并授权补齐 Windows 用户环境变量、使用廉价模型实测；框架交付后用户可继续扩充端点。保留完整调用账目，凭据不进入仓库和报告。
5. 用实测反例收敛阶段 3；每阶段更新本文，下一会话无需依赖本次聊天或 TEMP 内容恢复目标。
