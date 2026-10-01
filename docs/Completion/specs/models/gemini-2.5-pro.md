# gemini-2.5-pro 推理预算规格调查

- 核查日期：2026-10-01（Asia/Singapore）
- 调查方式：Google 官方 Generate Content thinking 指南与模型目录；无本次在线调用
- 适用表面：Google Developer API 原生 Generate Content
- 调查范围：推理预算与生命周期；本轮未逐项整理该模型输出上限及其他能力

## 官方事实

| 项目 | 已核查结果 |
| --- | --- |
| 精确 model-id | `gemini-2.5-pro` |
| reasoning 控制 | `generationConfig.thinkingConfig.thinkingBudget`，整数 |
| 预算范围 | 128–32,768 |
| 关闭控制 | 不支持关闭 |
| 默认与动态预算 | 未配置采用动态思考；thinkingBudget=-1 表达动态预算 |
| thinkingLevel | 不支持，不可套用 Gemini 3 的 level 投影 |
| 访问与生命周期 | 当前仅对曾使用过 2.5 的用户开放；官方明确尚未 deprecated |

预算、默认与关闭能力来自 [官方预算表](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thinking-budgets)；访问限制来自 [模型目录](https://ai.google.dev/gemini-api/docs/models) 与 [2026-09-18 更新](https://ai.google.dev/gemini-api/docs/changelog)。这些是核查日期的服务状态，不是长期可用承诺。

## 框架接入边界

原生协议不支持真正关闭思考，但现有 string-level mapper 不能表达数值预算对象。当前 Gemini 客户端已有 reasoning 配置入口；首片仅支持现代 level，保留本模型 ProviderDefault 的既有调用路径，显式预算另立切片。

不把数字塞进 WireLevel，不同时写 level 与 budget，不给 Low/Medium/High 推定统一 token 数；这些意图到预算的映射尚未设计或验证。输出上限没有本轮独立数据，不能从现代文本模型继承 65,536。

通用协议说明见 [Gemini Generate Content 规格说明](gemini-generate-content.md)，接入决策见 [Gemini 接入调研](../../gemini-model-specs-research.md)。没有本次资源 GET、生成、预算边界或关闭行为的在线证据。

