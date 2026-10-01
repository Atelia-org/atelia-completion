# gemini-3.1-flash-lite-image 模型规格调查

- 核查日期：2026-10-01（Asia/Singapore）
- 调查方式：Google 官方模型页与 Generate Content thinking 表；无本次在线调用
- 适用表面：Google Developer API 原生 Generate Content；图像生成变体
- 仓内用途：记录已核查的能力差异，作为禁止按文本模型前缀泛化的反例；不属于已实施的文本 level 首片

## 官方模型事实

| 项目 | 已核查结果 |
| --- | --- |
| 精确 model-id | gemini-3.1-flash-lite-image |
| 输入限值 | 65,536 tokens |
| 输出限值 | 4,096 tokens |
| 输出类型 | Image、Text |
| thinkingLevel | minimal/high；不支持 low/medium |
| 省略控制时的默认 | minimal |
| function calling | 不支持 |

限值、类型及 function calling 来自 [官方模型页](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-flash-lite-image)，推理档位与默认来自 [逐模型 thinking 表](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thinking-levels)。minimal 不保证思考完全关闭。

## 配置与验证边界

它与 [gemini-3.1-flash-lite](gemini-3.1-flash-lite.md) 不共享文本模型的 65,536 输出限值或 Low/Medium/High 集合。因此不能内建 `gemini-3*` 或 `gemini-3.1-flash-lite*` 统一补齐这些参数。宿主若提供通配，须自行确认规则实际覆盖的能力。

本轮没有设计此图像模型的框架 effort 映射，没有资源 GET、图像生成或完整协议适配的在线证据。记录原生事实不表示当前文本 GeminiClient 已支持图像输出。通用说明见 [Gemini Generate Content 规格说明](gemini-generate-content.md)。
