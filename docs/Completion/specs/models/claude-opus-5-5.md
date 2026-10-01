# claude-opus-5-5 reasoning effort 模型规格调查

- 调查日期：2026-10-01（Asia/Singapore, CST）
- 调查方式：Anthropic 官方文档检索（platform.claude.com；未做真实 API 在线验证）
- 仓内用途：为 `CompletionReasoningEffort` 新增 `XHigh` 挡位提供该模型的 wire 值映射依据

## 结论摘要

`claude-opus-5-5` 是 Anthropic Messages API 上的 adaptive thinking 模型，官方文档明确：**`output_config.effort` 支持 `low`、`medium`、`high`、`xhigh`、`max` 五挡，且 `medium` 是该模型的默认挡位**（当前模型阵容中唯一默认 `medium` 的模型，其余默认 `high`）。

- **`xhigh` 受支持**，官方定位为"长程工作（long-horizon work）"：超过 30 分钟的长时 agentic/编码任务、百万级 token 预算。这正是本仓需要新增 `XHigh` 挡位的直接依据。
- **thinking 恒开且无法关闭**：adaptive thinking 始终启用，`thinking: {"type": "disabled"}` 在所有 effort 挡位均返回 HTTP 400；`enabled`/`budget_tokens` 与 `between_tools` 同样返回 400。**effort 是该模型唯一的思考深度控制**，不存在 budget 数值控制。
- 本仓枚举中 `Low/Medium/High/XHigh/Max` 可与该模型直接一一映射；`Disabled` 无 wire 等价物。现已按 [model-specs](../../model-specs-design.md) 的库内政策归一为 Low；此意图不能作为严格关闭保证。
- `ProviderDefault` 映射为省略 effort 参数（服务端默认 `medium`）；显式传 `medium` 与省略行为完全一致。

## API surface 与参数

以下均为官方文档证实：

| 控制方式 | 参数写法 | 状态 | 备注 |
|---|---|---|---|
| 请求级 effort | 请求体顶层 `output_config: { "effort": "..." }` | GA | 所有支持模型均可用，**无需 beta header** |
| 会话中途变挡（per-message effort） | 在 `messages` 中插入 `role: "system"`、空 `content` 的消息并携带 `output_config.effort` | Beta | 需 header `mid-conversation-output-config-2026-07-01`；Opus 5.5 支持；保留 prompt cache |
| thinking 模式 | `thinking` 字段：省略或 `{"type": "adaptive"}`（两者等价） | GA | `{"type": "enabled", "budget_tokens": N}`、`{"type": "between_tools"}`、`{"type": "disabled"}` 均返回 400 |

- 不能把 `adaptive` 当作 effort 值传入：adaptive 是 thinking 模式，不是 effort 挡位。
- effort 影响响应中的**全部输出 token**：正文、工具调用参数、thinking 均受其调节。
- 模型基础规格：context window 1M tokens；max output 128K tokens（Batches beta 上限 300K）；可靠知识截止 2026-06；退休承诺不早于 2027-09-22；输入 text/image，输出 text。
- 各平台模型 ID：Claude API alias `claude-opus-5-5`；Amazon Bedrock `anthropic.claude-opus-5-5`；Google Cloud `claude-opus-5-5`；Claude Platform on AWS `claude-opus-5-5`；Microsoft Foundry `claude-opus-5-5`。

## effort 挡位矩阵

| 本仓枚举成员 | claude-opus-5-5 wire 值 | 模型是否支持 | 说明 |
|---|---|---|---|
| `ProviderDefault` | （省略 `output_config.effort`） | — | 服务端默认 `medium`；显式传 `medium` 与省略完全等价 |
| `Disabled` | 无等价值 | **不支持** | `thinking: {"type": "disabled"}` 在所有 effort 挡位均 400；adaptive thinking 无法关闭；适配层不得发送 |
| `Low` | `low` | 支持 | 最省 token；官方建议用于子代理等简单/延迟敏感任务 |
| `Medium` | `medium` | 支持 | **该模型默认挡位**；速度、成本与性能的平衡 |
| `High` | `high` | 支持 | 尽任务所需投入 token；是除 Opus 5.5 外其他支持 effort 模型的默认值 |
| `XHigh`（本轮新增） | `xhigh` | 支持 | 官方定位：长程 agentic/编码任务（超过 30 分钟）、百万级 token 预算 |
| `Max` | `max` | 支持 | 最高能力，token 花费不设约束；适用于最深的推理与最全面的分析 |

Wire 字段名：请求级为顶层 `output_config.effort`；不支持在 `thinking` 字段内传 effort（`thinking` 只接受模式，不接受数值或挡位）。

## 约束与注意事项（官方文档证实）

1. **effort 是行为信号而非硬 token 预算**：低挡位下模型在足够困难的问题上仍会思考，只是更少；不要把它当作严格预算上限依赖。
2. **`max_tokens` 是总输出硬上限**：thinking 加正文一起计入；高挡位官方建议设大 `max_tokens`（Opus 5.5 上限 128K），否则思考可能被截断。
3. **采样参数被禁**：`temperature`、`top_p`、`top_k` 任一非默认值即返回 400（每请求强制，与是否 thinking 无关）。
4. **预填充与强制工具调用被拒**：assistant 预填充（thinking 恒开）与 `tool_choice: {"type":"any"}` / `{"type":"tool"}` 在该模型上每请求 400。本库在网络前拒绝 RequiredAny/RequiredNamed，不把它们自动改为 Auto；宿主若更改业务要求须显式选择新合同。
5. **prompt cache 与 effort 变更**：请求之间改顶层 effort 会使已缓存前缀失效；需要在同一会话内变挡时用 per-message effort（beta），它保留 prompt cache。依赖缓存的长会话应选定挡位后保持不变。
6. **interleaved thinking 自动启用**：adaptive thinking 下工具调用之间自动交错思考，无需 beta header；模型可在工具调用间写 progress update。
7. **thinking 内容默认不可见**：默认 `display: "omitted"`——thinking block 的 `thinking` 字段为空，`signature` 携带加密思考内容用于多轮延续；完整思考输出需联系 Anthropic 销售；在正文中诱导模型复述内部推理可能触发 `reasoning_extraction` 拒绝类别。
8. **thinking block 跨模型兼容**：Opus 5.5 读取 Opus 5 及更早 Opus/Sonnet/Haiku 模型的 thinking blocks，不读取 Fable/Mythos 系列的 blocks。
9. **定价**：$4 / input MTok，$20 / output MTok；thinking token 按输出 token 计费。
10. **streaming**：支持流式响应，含 thinking trace 的流式输出。

## 首版内建与验收边界

2026-10-01 再次核查 [模型页](https://platform.claude.com/docs/en/models/opus-5-5/overview)、[Effort](https://platform.claude.com/docs/en/build-with-claude/effort) 与 [Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking)。内建仅登记精确 `claude-opus-5-5` 的 128,000 标准 Messages 输出限值、五个启用挡位、不可关闭与 forced-tool 禁令；不采用 Batch beta 的 300K，也不把同族前缀当相同能力。

ProviderDefault 始终省略，不调用 mapper；Disabled→Low 是本库政策。adapter 保留已有 adaptive 与 summarized 投影。`AnthropicModelSpecsTests` 使用真实 client 与内存 HTTP/SSE 验证七种意图及两种 forced 选择，无真实服务调用。旧四个 Opus 的迁移条目只声明原有输出限值，不借本页推定 reasoning 或工具支持。

## 官方证实 vs 未证实

**官方一手来源证实**：五挡支持列表与默认 `medium`；`xhigh`/`max` 官方定位；wire 字段 `output_config.effort` 与 per-message beta header 值；`thinking` 字段五种取值行为表（省略/adaptive 生效，enabled/budget_tokens、between_tools、disabled 均 400）；effort 影响 cache 失效；采样参数与强制工具调用/预填充的 400 行为；`max_tokens` 硬上限语义；定价与模型 ID。

**推测 / 未证实（官方来源未单独记载）**：

1. **各 effort 挡位的限速/tier 准入差异**：抓取的官方页面未记载按 effort 挡位区分的限速或 tier 门槛。
2. **Batch 折扣与 prompt caching 具体费率**：仅确认 Batches beta 输出上限 300K 与标准单价，缓存写入/读取费率未抓取 Pricing 页核实。
3. **per-message effort beta 的 GA 时间**：截至访问日期官方仅标注 "in beta"，未给出转正计划。
4. **在线实测**：本调查为纯文档检索，未用真实 API key 验证各挡位实际接受情况；接入适配层时建议按仓内约定用廉价在线调用做一次五挡冒烟验证（与离线 fixture 分开报告）。

## 来源清单

官方一手来源（访问日期均为 2026-10-01）：

1. Effort 文档（五挡定义与定位、默认值、`output_config.effort`、per-message beta、缓存失效规则、`max_tokens` 硬上限、tool use 行为）
   <https://platform.claude.com/docs/en/docs/build-with-claude/effort>
2. Thinking 文档（per-model thinking 配置表：Opus 5.5 adaptive 恒开、budget_tokens/between_tools/disabled 均 400；采样参数禁用；预填充与强制工具调用；display "omitted"；thinking blocks 跨模型兼容；流式 thinking）
   <https://platform.claude.com/docs/en/docs/build-with-claude/thinking>
3. Models overview（各平台模型 ID、context window 1M、max output 128K、定价 $4/$20、退休日期、知识截止）
   <https://platform.claude.com/docs/en/docs/about-claude/models/overview>
