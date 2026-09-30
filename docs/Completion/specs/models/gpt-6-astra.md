# gpt-6-astra reasoning effort 模型规格调查

- 调查日期：2026-10-01（Asia/Singapore, CST）
- 调查方式：官方文档与发布说明检索（未做真实 API 在线验证）
- 仓内用途：为 `CompletionReasoningEffort` 新增 `XHigh` 挡位提供该模型的 wire 值映射依据

## 结论摘要

`gpt-6-astra`（当前唯一快照 `gpt-6-astra`，2026-09-03 GA）是 reasoning 模型，官方模型页明确：**`reasoning.effort` 支持 `low`、`medium`、`high`、`xhigh`、`max` 五挡**。

- **`xhigh` 受支持**（Responses 与 Chat Completions 两个 surface 均可用），这正是本仓需要新增 `XHigh` 挡位的直接依据。
- **不支持 `none`**：设置后返回 HTTP 400（Responses 的 `reasoning.effort` 与 Chat Completions 的 `reasoning_effort` 同样适用）。
- **`minimal` 不受支持**：模型页支持列表未包含；官方迁移指引要求原用 `minimal` 的请求改用 `low`。官方未单独记载传 `minimal` 时的报错行为（见"未证实"）。
- 本仓枚举中 `Low/Medium/High/XHigh(新增)/Max` 可与该模型**直接一一映射**；`Disabled` 无 wire 等价物（`none` 返回 400），适配层需拒绝或另行映射（属主线程后续决策）。

## API surface 与参数

以下均为官方文档证实：

| Surface | 是否支持该模型 | reasoning 控制参数 | 备注 |
|---|---|---|---|
| Responses API | 支持 | `reasoning.effort`（嵌套字段） | 工具调用（function calling）只在此 surface 支持 |
| Chat Completions | 支持 | `reasoning_effort`（顶层字段） | **不支持 function calling**；支持不带工具的请求 |
| Batch | 支持 | 随所在请求体（同上） | 定价为 Standard 的 50% |

- 模型基础规格：context window 1,050,000 tokens；max input 922,000；max output 128,000；知识截止 2026-04-30；输入 text/image，输出 text。
- OpenAI Chat Completions 参考对 `reasoning_effort` 的全局值域描述为 `none, minimal, low, medium, high, xhigh, max`，并注明"Not all reasoning models support every value"，即具体取值以模型页为准。

## effort 挡位矩阵

| 本仓枚举成员 | gpt-6-astra wire 值 | 模型是否支持 | 说明 |
|---|---|---|---|
| `ProviderDefault` | （省略参数） | — | 不发送 effort 字段，由服务端默认行为决定；Astra 的默认值官方未明确记载（见"未证实"） |
| `Disabled` | 无等价值 | **不支持** | `none` 返回 HTTP 400；适配层不得将 `Disabled` 映射为 `none` 发送 |
| `Low` | `low` | 支持 | 官方迁移指引：从 `minimal`/`none` 迁移到 Astra 时从 `low` 起步对比效果 |
| `Medium` | `medium` | 支持 | |
| `High` | `high` | 支持 | |
| `XHigh`（本轮新增） | `xhigh` | 支持 | 官方定位：deep research、异步工作流、长程 agentic 任务；仅当评测显示收益可抵消额外延迟与成本时使用 |
| `Max` | `max` | 支持 | 官方定位：最复杂任务的最高推理量；已在用 `xhigh` 时可对比评测 `max` |
| （无对应成员） | `minimal` | **不支持** | 模型页未列出；迁移指引要求改用 `low` |

Wire 参数名按 surface 区分：Responses 写 `reasoning: { effort: "..." }`；Chat Completions 写 `reasoning_effort: "..."`。

## 约束与注意事项（官方文档证实）

1. **`none` 返回 HTTP 400**：对 Astra 设置 `reasoning.effort: "none"`（Responses）或 `reasoning_effort: "none"`（Chat Completions）都会被拒绝。
2. **effort 非 `none` 时采样参数被禁**：需移除 `temperature`、`top_p`、`top_logprobs`；Chat Completions 还需移除 `logprobs`。即 Astra 上所有受支持挡位（low…max）都不得同时传这些参数。
3. **工具调用绑定 Responses**：Chat Completions 对 Astra 不支持 function calling；需要工具的推理请求必须走 Responses。
4. **中途调整 effort**：Responses API 支持 `configuration_update` input item，在不重写前缀的情况下升降 effort 并保留 prompt cache 命中；官方迁移建议请求级 `reasoning.effort` 保持不变、用该机制变更，以利于缓存。
5. **reasoning mode 与 effort 独立**：`reasoning.mode: "pro"` 与 effort 可独立组合；pro 模式提高可靠性但增加延迟与 token 用量。
6. **xhigh/max 的使用建议**：官方建议仅在代表性评测证明质量收益值得额外延迟与成本时使用 `xhigh` 或 `max`；`xhigh` 适用场景为深度研究、异步工作流、长程 agent 运行。
7. **tier 与限速**：Free tier 不支持该模型；Tier 1–5 可用（模型页列出各 tier RPM/TPM/Batch 队列上限）。官方未记载 `xhigh`/`max` 存在按挡位的 tier 准入限制或单独限速（见"未证实"）。
8. **区域限制**：Fast mode 在 EU 数据驻留下不可用；Ultrafast mode 仅支持美国驻留与全球处理。这些是 serving mode 约束，与 effort 挡位无直接耦合。
9. **定价**：>272K input tokens 的请求按 2x 输入与缓存、1.5x 输出计费；缓存写入按未缓存输入价的 1.25x；Batch/Flex 为 50%，Fast mode 为 2x。

## 官方证实 vs 未证实

**官方一手来源证实**：五挡支持列表（含 `xhigh`）；`none` 返回 HTTP 400；Chat Completions 无 function calling；参数名 `reasoning.effort` / `reasoning_effort`；`minimal` 不在支持列表且迁移指引指向 `low`；effort 非 `none` 时的采样参数禁用；`configuration_update` 中途调整。

**推测 / 未证实（官方来源未单独记载）**：

1. **Astra 的默认 effort 值**：官方文档明确记载了 gpt-5.5 与 GPT-6.1 Sol 的默认值（medium），但未记载 Astra 的默认值。省略参数时的服务端行为未确认。
2. **`minimal` 的具体报错行为**：未记载传 `minimal` 是否同样返回 HTTP 400，仅能由支持列表与迁移指引推断为不支持。
3. **`xhigh`/`max` 的 tier 准入与限速差异**：未发现按 effort 挡位区分 tier 或限速的官方说明。
4. **在线实测**：本调查为纯文档检索，未用真实 API key 验证各挡位的实际接受情况；接入适配层时建议按仓内约定用廉价在线调用做一次五挡冒烟验证（与离线 fixture 分开报告）。

## 来源清单

官方一手来源（访问日期均为 2026-10-01）：

1. GPT-6 Astra Model 页（模型页，effort 支持列表与模型规格）
   <https://developers.openai.com/api/docs/models/gpt-6-astra>
2. Reasoning models guide — Reasoning effort（值域、`none` 返回 400、`xhigh`/`max` 定位、默认值记载、Chat Completions 无 function calling）
   <https://developers.openai.com/api/docs/guides/reasoning#reasoning-effort>
3. API deployment checklist — Set up reasoning.effort（GPT-6 家族挡位支持矩阵、迁移建议）
   <https://developers.openai.com/api/docs/guides/deployment-checklist#set-up-reasoningeffort>
4. Using GPT-6（latest model guide：Limitations、migration quickstart、采样参数禁用、configuration_update、区域限制）
   <https://developers.openai.com/api/docs/guides/latest-model>
5. Chat Completions Create 参考（`reasoning_effort` 全局值域含 `xhigh`、`max`，注明按模型支持）
   <https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create>
6. GPT-6 Astra 发布说明（GA 日期 2026-09-03、可用 endpoint、不支持 `none`、中途调整 effort）
   <https://openai.com/index/gpt-6-astra/>（发布日期 2026-09-03）

辅助来源（非一手，仅作交叉印证，不作为结论依据）：

7. openai-openapi 生成的官方 SDK 枚举（`ReasoningEffort` = none/minimal/low/medium/high/xhigh/max，与 API 参考一致）
   <https://github.com/openai/openai-openapi>（访问 2026-10-01）
8. Microsoft Azure OpenAI 文档（`xhigh` 自 gpt-5.1-codex-max 之后模型可用，含 Astra 一代；佐证而非依据）
   <https://learn.microsoft.com/en-us/azure/ai-foundry/openai/>（访问 2026-10-01）
