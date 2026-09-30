# deepseek-v4-1-flash reasoning effort 模型规格调查

- 调查日期：2026-10-01（Asia/Singapore, CST）
- 调查方式：DeepSeek 官方 API 文档检索（未做真实 API 在线验证）
- 仓内用途：`CompletionReasoningEffort` 各挡位在该模型上的 wire 映射依据

## 结论摘要

DeepSeek V4.1 Flash 的官方 API 模型 ID 是 **`deepseek-flash`**（旧 `deepseek-v4-flash` 等别名仍被接受并路由到 V4.1 Flash）。思考经 `thinking: {"type": "enabled"|"disabled"}` 开关，强度经 `reasoning_effort` 控制，**官方支持 `low` / `high` / `max` 三挡，默认开启思考、effort 为 `high`**。

- `Disabled` 有官方 wire 等价物（OpenAI 格式 `thinking: {"type":"disabled"}`；Anthropic/Responses 格式 `reasoning.effort: "none"`）。
- 官方在 Responses 兼容层接受 `minimal/medium/xhigh` 并归一（`minimal→low`、`medium/xhigh→high`）；即 `xhigh` 被接受但**行为上不是独立挡位**，等效 `high`。
- 本仓枚举 `Low/High/Max/Disabled` 可直接映射；`XHigh` 传入会被归一为 `high`（无独立行为）；`Medium` 在 OpenAI Chat Completions 上官方文档仅列三挡。

## API 参数与挡位矩阵

| 本仓枚举成员 | wire 值（OpenAI Chat / Anthropic / Responses） | 是否支持 | 说明 |
|---|---|---|---|
| `ProviderDefault` | （省略参数） | — | 默认思考开启、effort `high` |
| `Disabled` | `thinking:{"type":"disabled"}` / `reasoning.effort:"none"` | 支持 | OpenAI 格式经 thinking 开关关闭；Anthropic/Responses 格式 `none` 即关闭 |
| `Low` | `reasoning_effort:"low"` | 支持 | |
| `Medium` | `"medium"` | **Chat 未证实** | Chat 官方仅列 `low/high/max`；Responses 接受并归一为 `high` |
| `High` | `"high"` | 支持 | **默认挡位** |
| `XHigh` | `"xhigh"` | 接受但归一 | Responses 官方映射表：`xhigh→high`；非独立挡位 |
| `Max` | `"max"` | 支持 | 最高投入 |

官方"请求挡位 → 实际挡位"归一表（Responses/兼容层）：`minimal→low`、`low→low`、`medium→high`、`high→high`、`xhigh→high`、`max→max`、`ultra→max`。

## 注意事项

1. 思考模式下 `temperature`、`presence_penalty`、`frequency_penalty` 不支持（官方文档明确）。
2. 多轮对话中必须把 `reasoning_content` 回传（官方要求），本仓 reasoning payload 传递需遵循该契约。
3. Anthropic 兼容 API：`base_url` 指向 DeepSeek 兼容端点，effort 经 `output_config.effort`（`low/high/max`）与 `reasoning.effort`（`none/low/high/max`）。
4. 仓内当前 `XHigh` 在 DeepSeek 分支抛异常是合理保守行为：wire 上虽可能被归一接受，但没有独立 `xhigh` 行为，直接映射不会改变实际推理投入。

## 官方证实 vs 未证实

官方一手来源证实：模型 ID `deepseek-flash` 与旧名兼容；thinking 开关与 `reasoning_effort` 三挡、默认 high；Responses 挡位归一表；思考模式禁用采样参数；`reasoning_content` 回传要求。

未证实：OpenAI Chat Completions 上 `medium`/`xhigh` 的实际接受情况（官方文档未列）；在线实测各挡位。

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
