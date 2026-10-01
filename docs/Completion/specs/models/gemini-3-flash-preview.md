# gemini-3-flash-preview 模型规格调查

- 核查日期：2026-10-01（Asia/Singapore）
- 调查方式：Google 官方模型页与 Generate Content thinking 表；在线范围另列
- 适用表面：Google Developer API 原生 `v1beta/models/{model}:streamGenerateContent`
- 代码状态：此 ID 不在初始内建目录；下述映射仅为宿主扩展建议

## 官方模型事实

| 项目 | 已核查结果 |
| --- | --- |
| 精确 model-id | `gemini-3-flash-preview` |
| 版本状态 | Preview |
| 最大输出 | 65,536 tokens |
| reasoning 控制 | `generationConfig.thinkingConfig.thinkingLevel` |
| 原生挡位 | minimal/low/medium/high |
| 省略控制时的默认 | high |
| 完全关闭 thinking | 不支持；minimal 也不是关闭保证 |

ID、版本状态与输出上限来自 [官方模型页](https://ai.google.dev/gemini-api/docs/models/gemini-3-flash-preview)；挡位、默认与关闭能力来自 [Generate Content thinking 指南](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thinking-levels)。均为上述日期的核查结果。

## 框架映射建议

复用 ForSupportedLevels([Low, Medium, High], supportsDisabled:false)：ProviderDefault 省略 thinkingConfig；Disabled→Low；Low/Medium/High 分别发送 low/medium/high；XHigh/Max→High。这是宿主可配置的舍入政策，本 ID 尚未内建。ProviderDefault 保留该模型 high 默认，不固定改成 medium。

较早 Preview 作为对照记录，当前源码首批未内建。不要因 3.5 Flash 页列出该版本，就把其默认 high 改写成 medium。

maxOutputTokens 同时覆盖思考和正文，不能把 65,536 理解为正文保证额度。通用字段、别名及协议边界见 [Gemini Generate Content 规格说明](gemini-generate-content.md)。

## 在线证据与未验证项

2026-10-01：没有对该 ID 做真实资源查询或生成；本条为官方文档调查。

本次没有验证该模型完整档位矩阵或工具回放，未在线验证此 ID 的生产客户端扩展映射。完整方法与脱敏结果见 [实测记录](gemini-generate-content.md#在线探针记录) 和 [归档证据](evidence/2026-10-01-gemini-native-probe.json)。接入范围与流程见 [Gemini 接入调研](../../gemini-model-specs-research.md)。

