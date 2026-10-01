# deepseek-v4-1-flash reasoning effort 模型规格调查

- 调查日期：2026-10-01（Asia/Singapore, CST）
- 调查方式：DeepSeek 官方 API 文档检索（未做真实 API 在线验证）
- 仓内用途：`CompletionReasoningEffort` 各挡位在该模型上的 wire 映射依据

## 结论摘要

DeepSeek V4.1 Flash 的官方 API 模型 ID 是 **`deepseek-flash`**。本轮内建只登记该精确 ID，不将文件名或未逐条核查的旧名自动注册为别名。思考经 `thinking: {"type": "enabled"|"disabled"}` 开关，强度经 `reasoning_effort` 控制，**实际启用挡位为 `low` / `high` / `max`，默认开启思考、effort 为 `high`**。

- `Disabled` 有官方 wire 等价物（OpenAI 格式 `thinking: {"type":"disabled"}`；Anthropic/Responses 格式 `reasoning.effort: "none"`）。
- 官方在 Responses 兼容层接受 `minimal/medium/xhigh` 并归一（`minimal→low`、`medium/xhigh→high`）；即 `xhigh` 被接受但**行为上不是独立挡位**，等效 `high`。
- 2026-10-01 复核 [Chat API](https://api-docs.deepseek.com/api/create-chat-completion/) 已明确接受 `none/low/high/max`，兼容 `minimal→low`、`medium/xhigh→high`。本库 [model-specs](../../model-specs-design.md) 按实际集合将 `Medium/XHigh→High`，保留 `Low`，关闭仍使用已有 thinking 开关。

## API 参数与挡位矩阵

| 本仓枚举成员 | wire 值（OpenAI Chat / Anthropic / Responses） | 是否支持 | 说明 |
|---|---|---|---|
| `ProviderDefault` | （省略参数） | — | 默认思考开启、effort `high` |
| `Disabled` | `thinking:{"type":"disabled"}` / `reasoning.effort:"none"` | 支持 | OpenAI 格式经 thinking 开关关闭；Anthropic/Responses 格式 `none` 即关闭 |
| `Low` | `reasoning_effort:"low"` | 支持 | |
| `Medium` | 库发送 `"high"` | 兼容输入已证实 | Chat 接受 `medium` 并归一；库先映射为实际挡位 |
| `High` | `"high"` | 支持 | **默认挡位** |
| `XHigh` | 库发送 `"high"` | 兼容输入已证实 | Chat 接受 `xhigh` 并归一；不是独立行为挡位 |
| `Max` | `"max"` | 支持 | 最高投入 |

官方"请求挡位 → 实际挡位"归一表（Responses/兼容层）：`minimal→low`、`low→low`、`medium→high`、`high→high`、`xhigh→high`、`max→max`、`ultra→max`。

## 注意事项

1. 思考模式下 `temperature`、`presence_penalty`、`frequency_penalty` 不支持（官方文档明确）。
2. 请求携带 tools 时，需回传此前各轮 `reasoning_content`；不携带 tools 时，服务器忽略回传的 reasoning。这是 replay 合同，本轮不修改原生 payload 或协议身份。
3. Anthropic 兼容 API：`base_url` 指向 DeepSeek 兼容端点，effort 经 `output_config.effort`（`low/high/max`）与 `reasoning.effort`（`none/low/high/max`）。
4. 已知 `deepseek-flash` 不再沿用旧实现的 `Low→high` 与 `XHigh` 抛异常；规格先归一，再由 DeepSeek dialect 写 thinking 与 effort。未命中规格的模型仍沿用协议默认路径。

## 官方证实 vs 未证实

官方一手来源证实：模型 ID `deepseek-flash`；thinking 开关、实际三挡与默认 high；Chat 的 medium/xhigh 兼容归一；reasoning 回传取决于 tools。当前 Thinking Mode 指南说明不支持的采样参数被忽略，不一定触发错误。

未验证：本轮各挡位的真实在线接受情况，以及未逐条核查的旧模型别名。内存 HTTP/SSE fixture 仅验证库生成的请求和响应合同。

## 来源清单

官方一手来源（访问日期 2026-10-01，URL 已核验 HTTP 200）：

1. Thinking Mode 指南（thinking 开关、reasoning_effort low/high/max、默认 high、采样参数禁用、reasoning_content 回传）
   <https://api-docs.deepseek.com/guides/thinking_mode>
2. Responses API（reasoning.effort none/low/high/max、minimal/medium/xhigh 归一映射）
   <https://api-docs.deepseek.com/api/create-response>
3. Chat Completion API（OpenAI 兼容接口）
   <https://api-docs.deepseek.com/api/create-chat-completion>
4. 更新日志（V4 系列思考模式支持 low/high/max 三挡）
   <https://api-docs.deepseek.com/updates>
