# Gemini 3.1 Flash-Lite → 3.8 Flash 签名回放实验

日期：2026-09-24。范围：Google 官方 Gemini API、现有 `GeminiClient` 的
`v1beta/models/{model}:streamGenerateContent?alt=sse` 协议，不涉及 Interactions API。

## 协议选择

Google 目前也提供 `POST /v1beta/interactions?alt=sse`。它的请求与流事件结构不同，
且 [迁移指南](https://ai.google.dev/gemini-api/docs/migrate-to-interactions)
明确说 `generateContent` 仍受支持。
[Gemini 3.8 Flash 指南](https://ai.google.dev/gemini-api/docs/generate-content/latest-model)
仍给出 `generateContent` 用法。本次验证现有 client 的协议，不把 Interactions 的
`thought` step 或签名格式解释成 `generateContent` 的 content part。

[Generate Content 签名文档](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures)
要求回传收到的 `thoughtSignature`，指出非工具调用的流式响应可能把签名放在
空文本 part。现有 parser 和 replay 块保留这类 part。更广义的
[Gemini thinking 文档](https://ai.google.dev/gemini-api/docs/thinking)
也指出无状态模型切换时应继续传旧 thought 块，由后端处理兼容性；其所述
`thought` step 属于 Interactions 协议，不能直接当作本协议的请求结构。

## 客户端规则

`GeminiReplayBlock` 的 `Origin.ProviderId` 与 `Origin.ApiSpecId` 必须与目标
`GeminiClient` 完全相同；`Origin.Model` 保持原值，但不用于拦截模型切换。
仍验证本地 payload 的结构、`role=model` 以及可见正文和工具调用与 replay
payload 的一致性。服务器决定签名能否在目标模型上使用。

## 实际调用

使用运行时 `GEMINI_API_KEY`，官方基址
`https://generativelanguage.googleapis.com/`，没有保存请求体、签名或凭据。
以 `ATELIA_RUN_GEMINI_31_LITE_TO_38_FLASH_LIVE=1` 显式运行
`GeminiCrossModelThinkingLiveTests`：

| 阶段 | 模型 | models GET | streamGenerateContent POST | 结果 |
| --- | --- | --- | --- | --- |
| 生成 | `gemini-3.1-flash-lite` | 200 | 200 | Completed，正文长度 3，原生签名 1 个 |
| 续轮 | `gemini-3.8-flash` | 200 | 200 | Completed，正文长度 3，答案包含 `323` |

测试从第一轮 `GeminiReplayBlock` 读取签名，并检查第二轮实际 POST 的模型历史
中同一签名原样出现，数量也相同。第一轮生成与第二轮继续均通过生产
`GeminiClient`。签名 opaque，测试没有解密；`323` 也在第一轮可见正文中，
所以这个结果证明签名已回传且服务器接受了续轮请求，不能单独证明服务端
内部读取了签名承载的推理状态。

离线 Gemini 测试验证 3.1 → 3.8 原样投影，以及跨 provider / API spec 的
回放拒绝。此实验不改变 Galatea 的真实会话或任何既有持久状态。
