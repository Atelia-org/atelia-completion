# glm-5.3 reasoning effort 模型规格调查

- 调查日期：2026-10-01（Asia/Singapore, CST）
- 调查方式：Z.ai 官方文档检索（docs.z.ai；未做真实 API 在线验证）
- 仓内用途：`CompletionReasoningEffort`（ProviderDefault/Disabled/Low/Medium/High/XHigh/Max）与该模型 wire 值的映射依据

## 结论摘要

`glm-5.3`（含 GLM-5.3-FLASH）是 Z.ai Chat Completions API（`/api/paas/v4/chat/completions`）上的强制思考模型：**`reasoning_effort` 在 API 请求中仅接受 `low`、`high`、`max` 三个挡位，默认且推荐 `max`；传入其他任何值（含 `medium`、`xhigh`、`none`、`minimal`）都会报错**。

- `thinking.type` 仅支持 `enabled`（GLM-5.3 为强制思考，`disabled` 会报错），无法关闭思考。
- 本仓枚举中仅 `Low/High/Max` 可直接映射；`Medium` 与 `XHigh` **没有 wire 等价物，直发即错**，适配层需显式失败或另行映射（主线程后续决策）。
- `ProviderDefault` 映射为省略 `reasoning_effort`（服务端默认 `max`）；显式传 `max` 与省略行为一致。
- 官方文档另记载 Coding Plan 请求的宽容映射：`none/minimal/low→low`、`medium/high→high`、`xhigh/max→max`。这仅是特定产品面的行为，普通 API 请求不适用。

## API 参数与挡位矩阵

wire 字段：请求体顶层 `reasoning_effort`（字符串）；思考开关为请求体顶层 `thinking: { "type": "enabled" }`。`reasoning_effort` 仅 GLM-5.2 及以上模型支持。

| 本仓枚举成员 | glm-5.3 wire 值 | 模型是否支持 | 说明 |
|---|---|---|---|
| `ProviderDefault` | （省略 `reasoning_effort`） | — | 服务端默认 `max`（官方推荐挡位） |
| `Disabled` | 无等价值 | **不支持** | `thinking.type: "disabled"` 报错；强制思考模型无法关闭 |
| `Low` | `low` | 支持 | 官方定位：轻量推理（mild/lightweight reasoning） |
| `Medium` | `medium` | **不支持** | API 请求中任何其他输入都会报错；仅 Coding Plan 请求映射到 `high` |
| `High` | `high` | 支持 | 官方定位：增强推理（enhanced reasoning） |
| `XHigh` | `xhigh` | **不支持** | API 请求中报错（`xhigh` 是 GLM-5.2 的挡位）；仅 Coding Plan 请求映射到 `max` |
| `Max` | `max` | 支持 | **默认挡位**；官方定位：深度推理（deep reasoning） |

## 注意事项（官方文档证实）

1. **挡位集合与 GLM-5.2 不同**：GLM-5.2 支持 `none/minimal/low/medium/high/xhigh/max`（其中 `none/minimal` 停止思考，`low/medium→high`，`xhigh→max`）；GLM-5.3 收窄为 `low/high/max` 且思考恒开。做同族模型映射时不可复用 5.2 的规则。
2. **未支持挡位是显式报错而非降级**：官方明确"Any other input will result in an error"；适配层不应静默降级到最近挡位。
3. 文档示例端点为 `https://api.z.ai/api/paas/v4/chat/completions`（Z.ai 开放平台；bigmodel.cn 为同族 `/api/paas/v4` 路径）。未发现 GLM-5.3 有 OpenAI Responses 风格接口的官方记载。

## 官方证实 vs 未证实

**官方一手来源证实**：`reasoning_effort` 仅接受 `low/high/max`、默认 `max`、其他输入报错；`thinking.type` 仅 `enabled`（`disabled` 报错，GLM-5.3 强制思考）；`reasoning_effort` 仅 GLM-5.2+ 支持；Coding Plan 请求的挡位映射表；GLM-5.2 的七挡规则（对照用）。

**推测 / 未证实**：

1. 报错的具体 HTTP 状态码与错误格式（官方页未给出）。
2. 在线实测：本调查为纯文档检索，未用真实 API key 验证各挡位实际接受情况；建议接入时按仓内约定做廉价在线冒烟（与离线 fixture 分开报告）。

## 来源清单

官方一手来源（访问日期均为 2026-10-01）：

1. GLM-5.3 模型页（参数表：`thinking.type` 仅 `enabled` 且默认 `enabled`；`reasoning_effort` 取值 `low/high/max`、默认 `max`）
   <https://docs.z.ai/guides/llm/glm-5.3>
2. Deep Thinking 能力页（GLM-5.3 强制思考、`disabled` 报错；API 请求仅 `max/high/low`、其他输入报错；Coding Plan 映射；GLM-5.2 七挡对照；cURL 示例端点）
   <https://docs.z.ai/guides/capabilities/thinking>
