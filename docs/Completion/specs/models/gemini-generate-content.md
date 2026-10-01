# Gemini Generate Content 模型规格说明

- 核查日期：2026-10-01（Asia/Singapore）
- 适用表面：Google Developer API 原生 Generate Content；在线探针使用 v1beta/streamGenerateContent
- 用途：汇总各 Gemini 模型条目共用的协议事实与已完成实测，避免重复维护
- 状态：规格与证据记录；现代 level model-specs 切片已在当前源码实施

各精确模型的挡位、默认、限值和已验证范围见 [模型索引](index.md)。架构、七款内建范围和当前实现见 [Gemini 接入记录](../../gemini-model-specs-research.md#实施与验收记录)，官方协议事实与本库政策分开记录。

## 字段与协议边界

| 控制 | Generate Content REST 字段 | 已核查合同 |
| --- | --- | --- |
| 推理档位 | generationConfig.thinkingConfig.thinkingLevel | 现代模型推荐；支持集与默认因模型不同，较早模型使用会报错 |
| 数值预算 | generationConfig.thinkingConfig.thinkingBudget | 整数；2.5 各款范围、关闭与默认不同，见各模型条目 |
| 摘要可见性 | generationConfig.thinkingConfig.includeThoughts | 请求返回 thought 摘要；与投入档位分开 |
| 总输出硬上限 | generationConfig.maxOutputTokens | API 可省略；思考和正文共同计入，不保证正文独占额度 |

字段来自 [Generate Content API reference](https://ai.google.dev/api/generate-content#ThinkingConfig)。API enum 名称列为 MINIMAL/LOW/MEDIUM/HIGH，官方 REST 示例使用小写 low/medium；本次探针验证两个端点接受小写 low，不推定任意大小写均可用。[ThinkingLevel](https://ai.google.dev/api/generate-content#ThinkingLevel)、[REST 示例](https://ai.google.dev/gemini-api/docs/generate-content/latest-model)

Models 资源提供 outputTokenLimit 与 thinking 标志，未提供完整 level 集合、默认档或预算模式；只读 maximum 不足以自动决定思考控制。输出上限不能用输入限值代替。[Models API](https://ai.google.dev/api/models#Model)

当前 GeminiClient 优先使用选中本地规格的限值，缺值才沿缓存查询 Models，然后主动发送 maximum；这是本库政策，不是服务器要求每次先 GET。低硬上限可能在得到正文前用尽思考额度；调整 effort 与降低输出 cap 不是相同控制。[输出配置](https://ai.google.dev/api/generate-content#GenerationConfig)、[thinking 与总 token 上限](https://ai.google.dev/gemini-api/docs/generate-content/thinking#token-limits-and-max_output_tokens)

## 框架映射政策

对已核查支持 low/medium/high 的现代文本模型，推荐三挡子集：ProviderDefault 省略 thinkingConfig；Disabled→Low；Low/Medium/High 直映；XHigh/Max→High。Disabled→Low 是本库政策，Google 没有定义该框架枚举；minimal 也不是真正关闭。

Flash-Lite 默认 minimal，因此显式 Disabled→low 可能比服务端默认投入更多。框架尚无 Minimal 成员；宿主可在确认适用模型后用自定义 mapper 返回启用语义与 minimal 名称，不把它登记为 Disabled。2.5 的预算控制需要独立 typed 投影，不能把数值藏在 WireLevel 中。

七款初始内建范围由 [接入调研](../../gemini-model-specs-research.md#初始内建数据与推荐映射) 决定，并已登记于 BuiltinModelSpecs.GeminiGenerateContent；其他调查条目不会自动注册运行时规格。

## 模型命名与专用变体

精确 ID 与 `models/{id}` 是现有 client 接受的两种输入形式；将二者显式登记为共享规格，不等于改写请求 ID、Origin、fingerprint 或 capability cache key。通配按实际字符串匹配，不能自动跨两种形式。

latest 是会热切换的别名。更新日志曾记录 gemini-flash-latest 指向 3.5 Flash，但后续新型号发布不证明当前别名指向 3.8；首片不凭名字登记浮动别名。Pro 的 customtools 是单独优化端点，没有独立完整核查就不作等价别名。3.5 Flash 页与 3 Flash Preview 的版本组织亦不能抹掉逐模型表中不同默认值。[版本规则](https://ai.google.dev/gemini-api/docs/models#model-version-name-patterns)、[更新日志](https://ai.google.dev/gemini-api/docs/changelog)、[Pro 端点说明](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-pro-preview)

[Flash-Lite Image](gemini-3.1-flash-lite-image.md) 只有 4,096 输出上限、minimal/high 与不同工具能力；[3.8 Flash TTS](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash-tts) 不支持 thinking。它们证明系列前缀不能作为能力继承依据。TTS 本轮仅核查这个反例，未整理完整模型条目。

Generate Content 与 Interactions 的请求、事件及原生 payload 不互通；目录别名也不提供 Vertex 资源路径或 OAuth 适配。Google 推荐新开发考虑 Interactions，但 Generate Content 仍受支持。[协议迁移指南](https://ai.google.dev/gemini-api/docs/migrate-to-interactions)

## 在线探针记录

2026-10-01 完成 3 次模型资源 GET、4 次生成 POST，无重试。使用现成运行时凭据，认证经 x-goog-api-key；没有保存 key、请求头、签名、私人配置或原始响应正文。

脱敏观察已从临时 artifacts 归档到 [2026-10-01-gemini-native-probe.json](evidence/2026-10-01-gemini-native-probe.json)。它保留原探针结果，是归一后的观察数据，不是原始 HTTP 录制。后续追加探针应使用新日期或独立文件，保留这次记录的范围与数值。

### 请求方法

资源查询为 `GET https://generativelanguage.googleapis.com/v1beta/models/{id}`。生成请求为同一 origin 下的 `POST /v1beta/models/{id}:streamGenerateContent?alt=sse`。

公开短 prompt 为 `Calculate 17 times 19. Reply with just the decimal product.`；contents 使用 user role 的 text part。maxOutputTokens 使用各自 GET 返回的 65,536；省略 includeThoughts。四次生成分别设置 low、minimal、low、完全省略 thinkingConfig，其余请求内容相同。宿主探针使用 60 秒 CancellationToken；这没有改变生产库不设置 operation/idle timeout 的合同。

### 模型资源观察

| model-id | HTTP | outputTokenLimit | inputTokenLimit | thinking |
| --- | ---: | ---: | ---: | --- |
| gemini-3.1-flash-lite | 200 | 65,536 | 1,048,576 | true |
| gemini-3.5-flash-lite | 200 | 65,536 | 1,048,576 | true |
| gemini-3.8-flash | 200 | 65,536 | 1,048,576 | true |

### 生成观察

| model-id | thinkingConfig | HTTP 与原生 terminal | 响应 usage 已报告字段 |
| --- | --- | --- | --- |
| gemini-3.8-flash | thinkingLevel=low | 200 / STOP | prompt=17、candidates=3、thoughts=68、total=88 |
| gemini-3.8-flash | thinkingLevel=minimal | 400 / INVALID_ARGUMENT | 未报告 |
| gemini-3.5-flash-lite | thinkingLevel=low | 200 / STOP | prompt=17、candidates=3、total=20；thoughts 缺席 |
| gemini-3.5-flash-lite | 完全省略 | 200 / STOP | prompt=17、candidates=3、total=20；thoughts 缺席 |

三次成功均返回长度为 3 的文本。Lite 的缺席 thoughtsTokenCount 保持未知，不能推算成零或宣称真正关闭思考。一次短题不证明质量、典型成本或延迟排名。

这是先前直接 HTTP 探针，没有通过生产 GeminiClient 聚合，不能单独证明 mapper、factory 或本地校验工作。该探针未实测 Pro、完整档位、Lite 显式 minimal、预算、工具 continuation、Vertex 或 Interactions；后续生产客户端验收另列。

## 生产客户端接入验收

2026-10-01 新实现经 Release 构建后，显式启用 ATELIA_RUN_GEMINI_MODEL_SPECS_LIVE，使用生产 GeminiClient 执行两项真实测试、三次生成；没有 Models GET、重试或低硬上限。凭据仅从 GEMINI_API_KEY 运行时读取；签名和调用 ID 仅在内存比较，没有写入证据或日志。宿主每次通过两分钟 CancellationToken 控制期限。

| 阶段 | model-id | 实际控制 | HTTP / library terminal | library OutputTokens |
| --- | --- | --- | --- | ---: |
| 乘法短题 | gemini-3.5-flash-lite | Disabled→low；65,536 | 200 / Completed | 3 |
| 强制命名工具 | gemini-3.8-flash | Disabled→low；65,536；ANY/report_product | 200 / Completed | 16 |
| 工具结果续轮 | gemini-3.8-flash | Disabled→low；65,536；NONE | 200 / Completed | 4 |

Lite 返回 323；3.8 首轮报告 product=323，次轮携带原签名、匹配 functionResponse.id/name 及最终非空 user 文本，成功返回工具结果标记。归一后的脱敏观察见 [2026-10-01-gemini-model-specs-client.json](evidence/2026-10-01-gemini-model-specs-client.json)，请求与断言可追溯至 [在线测试](../../../../tests/Completion.Tests/Gemini/GeminiModelSpecsLiveTests.cs)。

这证明以上有限请求经生产客户端完成；factory 注入、完整映射矩阵和本地拒绝由离线 fixture 验证。其他型号、档位、非法末轮/prefill 的服务端拒绝、minimal/budget、Vertex 与 Interactions 未在本轮在线覆盖；不从单次 token 数推断典型成本或质量。

## 回放与未解决的协议核验

thought:true 的文本摘要应与正文分离。当前 parser 尚未提供该分离，effort 接入继续省略 includeThoughts；思考签名和 Origin 属于回放合同，不是档位配置。[thought summaries](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thought-summaries)、[thought signatures](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures)

3.8 迁移指南提出末轮 user 非空、禁止 model prefill，并把 FunctionResponse 关联标识称作 call_id；本轮重读 REST reference 仍定义 id/name/response。当前 converter 使用 id/name，与 reference 一致，且以上有效工具续轮在线接受此形式；没有据指南措辞改 wire 字段。非法轮次的远端拒绝与 call_id 的全部适用语境仍未解决，不能从成功续轮推定。[3.8 migration checklist](https://ai.google.dev/gemini-api/docs/generate-content/latest-model)、[FunctionResponse reference](https://ai.google.dev/api/generate-content#FunctionResponse)

官方调查、原生探针与生产客户端接入验收均标注于 2026-10-01，三组证据分别保存，不把先前探针当作新增功能的在线验收。
