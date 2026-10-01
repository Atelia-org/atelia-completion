# Gemini 客户端模型规格接入调研

日期：2026-10-01。范围：Gemini 原生 `v1beta/streamGenerateContent` 客户端、model-specs 层及逐模型证据。状态：现代 level 切片已实施；调研时的源码基线与接入决策保留如下，当前实现及验收见末节。先前 3 GET/4 POST 原生探针与本轮生产客户端 0 GET/3 POST 分别归档，官方事实、实测与本库政策分别说明。

**调查结论是局部重构，现已按此落地。** 调研时 Gemini 已有输出上限 discovery，却没有 reasoning effort 配置入口；两者组成第三个 model-specs 纵向切片。Pro、Flash、Flash-Lite 的数据支持复用不可变目录、现有 mapper 和客户端作用域，接入现代 thinking-level 投影。初始目录登记已核查的六款 Stable 文本模型与当前 Pro Preview；没有扩大公共规格结构或引入通用参数引擎。

这里“对齐”不是把 Gemini 的全部控制都翻译成一个字符串：现代模型采用 `thinkingLevel`，较早模型采用数值 `thinkingBudget`；API 可选的输出限值与本库主动设置 maximum 的政策也要区分。协议投影、签名回放、完成状态和取消仍由现有 Gemini adapter 负责。

## 实施前实现与实际缺口

此表保存调研时的源码基线；options、level DTO、本地限值及 factory 缺口现已补齐，parser 摘要输出仍延期。

| 当前证据 | 实际行为 | 对重构的含义 |
| --- | --- | --- |
| [GeminiClient](../../src/Completion/Gemini/GeminiClient.cs) | 构造只接收 API key 和 HttpClient，没有 ReasoningEffort 或 ModelSpecs；每次请求先经过 maximum cache | 尚无业务投入意图入口；已知模型也不能用本地规格跳过首次 Models GET |
| 同一 client 的 FetchModelMaximumAsync | 查询 `v1beta/models/{id}`，严格读取正整数 `outputTokenLimit` | discovery、成功缓存、失败分类已有实现，可直接复用 |
| [GeminiApiModels](../../src/Completion/Gemini/GeminiApiModels.cs) | GenerationConfig 只有 required int MaxOutputTokens，没有 ThinkingConfig | 当前并非已有 effort 映射待迁移；需要新增实际请求投影 |
| [GeminiMessageConverter](../../src/Completion/Gemini/GeminiMessageConverter.cs) | 把解析的 maximum 写入 maxOutputTokens；映射工具、历史和 replay | 输出限值与消息投影已分离，但本地工具及历史验证目前在 Models GET 后发生 |
| 同一 converter 的 BuildToolConfig | RequiredAny/RequiredNamed 使用 ANY；AllowParallelToolCalls=false 抛 NotSupportedException | 可提前执行确定性校验；不能借此次映射削弱业务工具要求 |
| client 的 NormalizeModelPath | 接受短 ID 或 `models/` 资源名，再对后缀 URI escape | 保留路径规则；规格 lookup 仍按实际传入 ID，不能顺手建立全局 canonical ID |
| [DefaultCompletionClientFactory](../../src/Completion/CompletionConnections.cs) | 目前只创建 Chat、Responses、Anthropic；selector 只传给 Chat/Anthropic | 单改 Gemini 构造还不能让连接配置使用它；需补 gemini 分支与 selector 传递 |
| [GeminiStreamParser](../../src/Completion/Gemini/GeminiStreamParser.cs) | 保留 thoughtSignature，但文本 part 没有按 thought 标记分离 summary | effort 接入不要顺手打开 includeThoughts，避免把思考摘要并入正文 |

原 [model-specs 设计](model-specs-design.md) 把 Gemini 接入留待有具体需求后再做。这次已出现可核查的实际消费者，不需要推翻原设计。需要局部澄清的是“可省略上限的协议保持省略”这句话：Gemini 当前已经主动查询并发送 maximum，不能误读为所有协议当前都省略此字段。

## 官方合同与协议边界

### 现有 Generate Content 仍有接入价值

Google 的 [迁移指南](https://ai.google.dev/gemini-api/docs/migrate-to-interactions) 推荐新开发使用 Interactions，同时说明 generateContent 仍受支持。因此，接入 model-specs 不以换协议为前提。Interactions 的 `generation_config`、thought steps 和流事件不能搬进当前 DTO 或 parser。

本次使用 [Generate Content thinking 指南](https://ai.google.dev/gemini-api/docs/generate-content/thinking) 核查参数。通用 `/docs/thinking` 页主要描述 Interactions，不能仅凭同名概念推定两个表面的字段和默认值相同。

### 模型事实与实测归档

逐模型事实已整理到 [模型规格索引](specs/models/index.md)：八款现代文本模型分别记录精确 ID、状态、输出上限、推理档位及默认；三款 2.5 单独记录预算与生命周期；Flash-Lite Image 单独保存族规则泛化的反例。后续更新模型事实优先修改这些条目，此报告保留接入决策与实现边界。

现代文本模型的三种默认值为 medium、minimal、high，不能用统一默认取代 ProviderDefault。共同字段、REST 小写证据、输出与思考共享额度、浮动别名及协议差异见 [Gemini Generate Content 规格说明](specs/models/gemini-generate-content.md)。2.5 的预算控制仍在服务，延期依据是首片消费者与投影结构不同，不能称其已退役。

同日已完成的 3 次资源 GET、4 次生成 POST 已归档为 [脱敏 JSON](specs/models/evidence/2026-10-01-gemini-native-probe.json)，方法和结果见 [在线探针记录](specs/models/gemini-generate-content.md#在线探针记录)。它确认三个确切模型的 65,536 限值、3.8 接受 low 并拒绝 minimal、3.5 Lite 接受 low 与省略控制。缺席的 thoughts 字段保持未知。

这些是原生 HTTP 探针，不是拟议 GeminiClient 功能的验收；完整档位、Pro 与工具 continuation 均未实测。本次整理未重新调用模型，证据已保存在 docs 内，不再依赖被 Git 忽略的临时 artifacts。

## 最小接入方案（现代 level 已实施）

### 复用规格目录与固定 adapter

GeminiClientOptions 提供 ReasoningEffort 和 ModelSpecs，默认分别为 ProviderDefault、未指定目录。未指定采用 BuiltinModelSpecs.GeminiGenerateContent；Empty 禁用内建知识；显式目录整体替换。遵守精确优先、最长前缀、全局规则及完整规格选择，不增加另一套 resolver。

新增内容限于 client/options、Gemini DTO/converter、内建数据、factory 与测试。CompletionModelSpec、ReasoningEffortMapping 和 mapper 接口无需为现代 level 控制改变。adapter 固定把启用结果的 WireLevel 投影为 `generationConfig.thinkingConfig.thinkingLevel`；名字必须可由此原生协议消费，不能默认接受标准 enum 中不存在的厂商自造名称。

模型知识只选择值，不选择任意字段结构。没有匹配到已知 thinking 规格的 2.5 模型不能因为名称看起来像 Gemini 就自动走 level 投影。

### 初始内建数据与推荐映射

初始目录推荐登记规格库中的六款 Stable 文本模型及 gemini-3.1-pro-preview，每条限值为 65,536；这是扩大调查后的数据覆盖选择。它们都支持 low/medium/high，可共用同一个不可变 mapper 和 spec。ProviderDefault 不调用 mapper，因此共用 spec 不会抹掉 medium、minimal、high 三种服务端默认。较早的 gemini-3-flash-preview 留作已核验的对照数据，首批无需登记；不因已经调查便纳入所有专用或历史端点。

下表对上述七款使用相同映射，是本库推荐政策，不是厂商承诺不同 effort 的成本或质量相同：

| 业务意图 | 映射或投影 |
| --- | --- |
| ProviderDefault | 省略 thinkingConfig，保留该模型服务端默认 |
| Disabled | EffectiveEffort=Low，wire=low |
| Low | low |
| Medium | medium |
| High | high |
| XHigh | high |
| Max | high |

使用现有 ForSupportedLevels([Low, Medium, High], supportsDisabled:false)。缺失 XHigh/Max 向中间舍入，Disabled 落到框架最低启用挡位。这延续用户已接受的 Disabled 不支持就舍入到支持的 Low 的政策。官方仅提供原生档位，没有规定框架的 Disabled 应映射为 low；这项决定属于本库。

Flash-Lite 以及 3.5/3.6 Flash 多出 minimal，需要明确框架与原生挡位集合不是一一对应。首个切片选取 low/medium/high 这个完整可用子集，不声称已经表达所有原生挡位；Flash-Lite 的 ProviderDefault 仍可以得到服务器的 minimal 默认。Disabled→Low 有可能比该默认投入更多，这是统一业务政策与服务器默认不同的结果，不应隐瞒。

不建议使用 ForNamedLevels({Disabled:"minimal", ...})，它会把非关闭值标成 Disabled；也不建议把 Low 常规映射成 minimal，因为这会失去用户对原生 low 的表达。宿主明确需要“Disabled 尽可能降低投入”时，可以用自定义 mapper 将该输入返回 `(Low, "minimal")`，其余交给三挡 mapper。这是可选的宿主政策，不是首版内建默认；adapter 消费一次映射结果，不重新套 mapper。无需为这个候选增强新增 Minimal 枚举。

这些精确条目只扩大已核查的数据，无需新的公共字段或 mapper 类型。测试以 3.8 Flash、3.5 Flash-Lite、3.1 Pro Preview 覆盖三种服务端默认，并保留仓内已使用 3.1 Flash-Lite 的用例；其他精确 ID 验证目录数据即可。是否进一步暴露原生 minimal，在出现明确业务需求时局部决定。

### 未知模型的透传边界

ProviderDefault 在所有目录状态下都省略显式 thinking 控制，绝不调用 mapper；保留既有未知模型的 maximum 查询与生成路径。

对于非默认 effort，首个切片要求选中规格提供 mapper。未命中或条目缺 mapper 时，在 GET 前明确报告不支持此配置，例如 NotSupportedException；不能悄悄丢弃意图，也不能把 XHigh/Max 或 Disabled 伪造为原生 thinkingLevel。Gemini 当前没有可以保留的既有 effort 默认映射，且 level/budget 不同，因此这不是撤掉已有透传能力。

宿主若知道某代理上的未知 ID 遵循 level 合同，可通过精确 ID 或通配规格提供 mapper。这里“未知模型尽量透传”仍保留 ProviderDefault 路径；显式新控制必须有可消费的协议知识，类似 Chat Unsupported dialect 的明确拒绝。不要为追求统一而引入 guess-by-prefix 或“忽略不支持参数”的成功路径。

非法 mapper enum、空名称、启用结果缺名称，或把 Disabled 语义结果配成 minimal/none 等无法表达的关闭控制，均是扩展配置错误；首个 level adapter 不声明支持真正 Disabled。能够表达的低投入 fallback 应返回启用语义。

### 本地限值与查询顺序

推荐流程为：

1. 使用实际 Request.ModelId 查询目录，得到一条完整规格。
2. 入口处理 ProviderDefault，或者调用 mapper 并验证 Gemini level 投影可消费性。
3. 完成确定性的工具、历史和 replay 投影校验，发生拒绝时尚未发 GET。
4. 规格 OutputTokenLimit 有值时直接采用；否则按实际 ID 沿现有 ProviderModelMaximumCache 查询 outputTokenLimit。
5. 写入 maxOutputTokens 与 thinkingConfig，发送原有 streamGenerateContent POST。

不必用一个假的正整数 maximum 先骗过 converter 再覆盖。可以让 converter 先完成不依赖限值的投影，把填充限值留到查询后；也可以分离纯校验函数。两者都不需要 public preview DTO 或通用请求计划。

精确条目缺限值时不能从族规则借值；同理，有限值而缺 mapper 的条目不能从更宽规则借 mapper。宿主配置应声明完整选定规格。

GET 非 2xx、畸形成功和取消保留现有异常合同；生成被拒后不调整数字或换规则重试。GET 已发生后，失败不能变成 CompletionRequestRejectedException。本地组合拒绝若采用该类型，则必须全部在 credential/network dispatch 前完成，并用固定安全理由。

### 命名与 factory 集成

内建可使用 WithModels(["gemini-3.8-flash", "models/gemini-3.8-flash"], spec)，为推荐七款分别登记同样两种明确形式。这是现有 client 接受的输入形式共享规格，不改变 Request.ModelId、fingerprint、Origin.Model 或 cache key。更换一个精确条目不会改变另一个。

未知短名与 `models/` 名仍可分别产生 capability 查询；不要因为 URI 指向相似资源就顺手合并缓存。通配规则也按原始字符串匹配：`gemini-*` 不会自动匹配 `models/gemini-*`。内建不增加族通配；用户有意配置两种模式时分别登记。

这一限制有具体模型反例：gemini-3.1-flash-lite-image 的输出上限为 4,096，thinking 只支持 minimal/high，且不支持 function calling。不能内建 `gemini-3*` 或 `gemini-3.1-flash-lite*` 统一填 65,536 与三挡 mapper；宿主的通配仍是宿主声明的适用假设。[该模型页](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-flash-lite-image)、[逐模型 thinking 表](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thinking-levels)

浮动 `latest` 会被服务器热切换，不登记为已知快照的别名。release notes 曾记录 gemini-flash-latest 指向 3.5 Flash，后续型号发布不能证明该别名当前已指向 3.8。Pro 的 customtools 是单独优化的端点，也不据名称自动继承普通 Pro 的全部规格。需要这些名字的宿主应核验后显式登记。[版本规则](https://ai.google.dev/gemini-api/docs/models#model-version-name-patterns)、[release notes](https://ai.google.dev/gemini-api/docs/changelog)、[Pro 的 customtools 说明](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-pro-preview)

DefaultCompletionClientFactory 增加 kind=gemini 分支，沿现有 ReasoningEffort 配置创建 GeminiClient，并在构造时把 modelSpecsSelector 的结果传入。现有 connection 字段已经可以表达这些内容，无需新 JSON 版本；仍需测试严格 loader 到 factory 的整条使用路径，不能只测 options。

当前路径是 `v1beta/models/...`，认证是 x-goog-api-key。规格目录和别名列表不提供 Vertex 资源路径、OAuth 或其他协议适配；不能因可登记 Vertex 名称便宣称 GeminiClient 已支持 Vertex。

## 本轮保留的行为与后续扩展条件

不从 Claude 复制“启用 thinking 就拒绝 forced tool choice”的规则。首个切片保持 Gemini 当前 ANY 与指定函数投影；明确登记 false 的模型规格可以拒绝 forced 要求，其他已存在的不支持组合仍检查。没有逐模型证据时不增加内建 true/false，也不把 RequiredNamed 自动改成 Auto。

includeThoughts 是响应可见性配置，与 reasoning effort 分开。当前 parser 未分离 thought:true 的文本，首个切片继续省略 includeThoughts；若以后提供摘要输出，必须同时处理正文、原生 payload 与 usage，另立有实际消费者的切片。[Generate Content thinking](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thought-summaries)

签名与回放仍保持当前 provider/API 身份门禁及模型切换规则，不能因目标 effort 不同而删签名、把签名当作 level 知识或增加 catalog hash 恢复门禁。[Generate Content thought signatures](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures)、[仓内历史实验](experiments/2026-09-24-gemini-flash-lite-to-flash-thinking.md)

最新模型的完整协议兼容仍需要独立验收。3.8 迁移指南还提出末轮 user 非空、禁止 model prefill，并把 FunctionResponse 关联标识写作 call_id；REST reference 则仍定义 id。当前 converter 投影 id/name，与 reference 一致；不能只因指南措辞不同就改字段或宣布现实现有缺陷。先前短题探针没有覆盖工具 continuation；本轮实施已在线验证有效工具续轮与最终非空 user 文本，非法轮次的远端拒绝仍未穷举。它们属于固定 adapter 的协议合同，不增加 model-spec 字段。[3.8 migration checklist](https://ai.google.dev/gemini-api/docs/generate-content/latest-model)、[FunctionResponse reference](https://ai.google.dev/api/generate-content#FunctionResponse)

2.5 数值预算是一个真实的后续扩展点。出现宿主需求时再增加明确的 typed budget 结果与 Gemini 固定投影，校验模型范围及预算和输出限值的关系；不要用 WireLevel="1024" 这样的字符串暗号、不同时写 level 和 budget、不用统一固定数字冒充厂商默认。

Interactions 接入属于新协议客户端，需要自己的请求、流事件、终止与 replay 设计。Google 的推荐可以支持后续评估，但不能在 model-specs 重构中悄悄切换 endpoint 或沿用当前 ApiSpecId。

## 验收与实施成本

| 应新增的验收 | 需要证明 |
| --- | --- |
| 代表模型的七种意图及目录数据 | 原生 DTO 字段正确；Disabled 仍用启用语义；ProviderDefault 不调用 mapper，分别保留三种默认；七个精确条目与限值有依据 |
| 未知、Empty、整条替换 | 默认控制省略；显式无 mapper 在 GET 前失败；不借通配字段；不偷偷恢复内建知识 |
| 输出限值 | 内建和宿主精确/前缀/全局限值不发 GET；选中缺值才查询；不引入 32,768 估值或 POST 后重试 |
| 多 ID 与 URI | 两种输入形式命中明确规格，路径仍按既有规则转义；实际 ID、Origin 与 cache 不被 canonicalize |
| 前置校验 | 不支持的并行要求、非法 mapper 与 forced false 均在 GET 前失败；HTTP/协议失败维持原分类 |
| 旧合同与最新模型协议验收 | replay roundtrip、tool adjacency、terminal、usage unknown、共享查询清理和 caller token 保持；区分离线回归与当前工具 continuation/轮次约束的在线证据 |
| factory 与严格连接入口 | gemini 分支实际消费 ReasoningEffort 与目录；selector 只在创建时调用；无 schema 版本变化 |

判断：**现代 level 切片复杂性低到中等且局部**。目录和舍入无需重写；主要新增 DTO、投影和构造入口，最大风险是把 minimal 当关闭、误用协议文档、或者在 GET 后仍报本地拒绝。若同时要求 2.5 budget、摘要输出、Vertex 与 Interactions，则会跨越多个合同，推荐分别实施。

前次调研按 CI 关闭全部 live opt-in 与 OPENROUTER_API_KEY，在 Windows 执行 Release 测试，过滤 `FullyQualifiedName~Gemini` 且排除 LiveE2E/LocalE2E：**77 通过，0 失败，0 跳过**。这是实施前 client/converter/parser/replay 与 capability 的行为基线；本轮新增映射的当前验收另列如下，不能用该历史数字代替。

调研阶段的独立官方调查和 3 GET/4 POST 原生实测已归档；它们作为实施依据，没有充当生产客户端验收。实际接入及新证据如下。

## 实施与验收记录

当前工作区已落地 [GeminiClientOptions](../../src/Completion/Gemini/GeminiClientOptions.cs)、[GeminiClient](../../src/Completion/Gemini/GeminiClient.cs)、thinkingConfig DTO、消息投影前移、[七款内建数据](../../src/Completion/ModelSpecs/BuiltinModelSpecs.cs) 及 factory gemini 分支。V2/V3 严格 loader 到 factory 的 fixture 验证 ReasoningEffort 和一次性目录选择，无连接 schema 变更。

本轮实际先 ProjectRequest 完成工具、历史和 replay 投影，再取选中限值或查询 maximum，最后填 generationConfig；没有假的占位限值。mapper 只消费一次，禁用结果、缺名称、非法 enum/原生名字在 HTTP 前报配置错误；非默认 effort 缺 mapper 抛 NotSupportedException。ForcedToolChoiceSupported=false 使用固定安全 reason model_specs.incompatible_tool_choice；null/true 不继承 Claude 的 thinking 禁令。内部最大值 wrapper 仍用于既有 converter fixture，生产 client 走前置投影路径。

Windows / .NET SDK 10.0.201：Release Rebuild 0 警告、0 错误；受影响离线测试 190 通过，完整离线回归 1,064 通过、3 项 Unix 权限/所有权场景跳过。测试覆盖四款代表模型七种意图、全部七个 ID 的两种形式、Empty/整条替换、精确不合并通配字段、宿主 minimal、非法 mapper、forced 要求、HTTP 前校验、实际 ID 缓存及签名续轮。

新增独立开关 ATELIA_RUN_GEMINI_MODEL_SPECS_LIVE，CI 默认 0。本轮设为 1 执行 [GeminiModelSpecsLiveTests](../../tests/Completion.Tests/Gemini/GeminiModelSpecsLiveTests.cs)，两项真实测试通过，合计 0 GET / 3 POST，均 HTTP 200 / Completed，thinkingLevel=low、maxOutputTokens=65,536。Lite 覆盖 Disabled→Low；3.8 覆盖强制命名工具与含最终 user 文本的签名续轮，functionResponse.id/name 与先前 functionCall 一致并被服务器接受。脱敏观察见 [生产客户端证据](specs/models/evidence/2026-10-01-gemini-model-specs-client.json)，原探针文件未覆盖。

该实测确认这一有效工具轮次可用，未穷举非法末轮/prefill 的远端拒绝；不能据此解释指南 call_id 的全部用途或宣称新增了轮次门禁。没有验证 Pro、其余型号完整档位、预算、摘要、Vertex 或 Interactions；这些原延期边界保留。没有 Pack、提交、推送或发布；包元数据与 README 未修改。
