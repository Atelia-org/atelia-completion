# gemini-3.1-pro-preview 模型规格调查

- 核查日期：2026-10-01（Asia/Singapore）
- 调查方式：Google 官方模型页与 Generate Content thinking 表；在线范围另列
- 适用表面：Google Developer API 原生 `v1beta/models/{model}:streamGenerateContent`
- 代码状态：当前源码已内建此 ID 及 models/ 资源形式；下述框架映射已实施

## 官方模型事实

| 项目 | 已核查结果 |
| --- | --- |
| 精确 model-id | `gemini-3.1-pro-preview` |
| 版本状态 | Preview |
| 最大输出 | 65,536 tokens |
| reasoning 控制 | `generationConfig.thinkingConfig.thinkingLevel` |
| 原生挡位 | low/medium/high；minimal 不支持 |
| 省略控制时的默认 | high |
| 完全关闭 thinking | 不支持；minimal 也不是关闭保证 |

ID、版本状态与输出上限来自 [官方模型页](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-pro-preview)；挡位、默认与关闭能力来自 [Generate Content thinking 指南](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thinking-levels)。均为上述日期的核查结果。

## 已实施的框架映射

复用 ForSupportedLevels([Low, Medium, High], supportsDisabled:false)：ProviderDefault 省略 thinkingConfig；Disabled→Low；Low/Medium/High 分别发送 low/medium/high；XHigh/Max→High。这是已实施的本库舍入政策；不是厂商对框架枚举的定义。ProviderDefault 保留该模型 high 默认，不固定改成 medium。

这是核查日期时目录中的当前通用文本 Pro。customtools 是单独优化的端点，未独立核查全部参数，不自动作为本条别名登记。

maxOutputTokens 同时覆盖思考和正文，不能把 65,536 理解为正文保证额度。通用字段、别名及协议边界见 [Gemini Generate Content 规格说明](gemini-generate-content.md)。

## 在线证据与未验证项

2026-10-01：没有对该 ID 做真实资源查询或生成；本条为官方文档调查。

本条原生探针不充当生产客户端验收；本轮生产客户端的有限在线验证另见 [接入验收](gemini-generate-content.md#生产客户端接入验收)。没有逐模型在线验证完整档位矩阵。完整方法与脱敏结果见 [实测记录](gemini-generate-content.md#在线探针记录) 和 [归档证据](evidence/2026-10-01-gemini-native-probe.json)。接入范围与流程见 [Gemini 接入调研](../../gemini-model-specs-research.md)。
