# claude-sonnet-5-5 模型规格

- 核查日期：2026-10-11（Asia/Singapore）
- 适用表面：Claude API 标准 Messages streaming；仅登记精确 `claude-sonnet-5-5`
- 证据类型：官方文档与仓内离线 HTTP/SSE fixture；未进行真实模型调用

## 官方事实

[模型页](https://platform.claude.com/docs/en/models/sonnet-5-5/overview)列出 Claude API ID `claude-sonnet-5-5`，发布日期 2026-09-28，标准输出上限 128,000 token；Batch API beta 的 300K 上限不适用于本条目。

[Effort 文档](https://platform.claude.com/docs/en/build-with-claude/effort#recommended-effort-levels-for-claude-sonnet-55)确认 `output_config.effort` 支持 `low/medium/high/xhigh/max`，默认 `high`，顶层参数无需 beta header。

[更新说明](https://platform.claude.com/docs/en/models/sonnet-5-5/whats-new-sonnet-5-5#breaking-changes)确认：

- `thinking: {"type":"disabled"}` 返回 400；`between_tools` 可以关闭前置 thinking，但仍允许工具之间的 progress thinking，只接受 `low/medium/high`，且不能携带 `display`。
- `tool_choice` 的 `any` 与 `tool` 在所有请求上都返回 400；`auto/none` 可用。

[Thinking 文档](https://platform.claude.com/docs/en/build-with-claude/thinking#turn-thinking-on)确认默认 adaptive thinking，省略配置时 `display` 默认为 `omitted`；可以显式发送 `adaptive` 与 `display: "summarized"`。

## 本库登记与政策

`BuiltinModelSpecs.AnthropicMessages` 登记 `OutputTokenLimit = 128_000`、五个启用挡位以及 `ForcedToolChoiceSupported = false`。

| 业务意图 | 本库 wire 投影 |
| --- | --- |
| ProviderDefault | 省略 thinking 与 output_config，保留服务端默认 high |
| Disabled | adaptive + summarized，effort=low；这是库内降挡政策，不表示关闭 thinking |
| Low / Medium / High / XHigh / Max | adaptive + summarized，effort 分别为 low / medium / high / xhigh / max |

现有 adapter 没有 `between_tools` 投影；本次只补目录数据，不新增 thinking 模式或 public API。RequiredAny/RequiredNamed 在任何 HTTP 前拒绝，不修改调用者工具要求。请求原样发送精确 ID，不登记拼写变体、日期猜测、平台前缀或同族通配规则。

## 验证边界

`AnthropicModelSpecsTests` 用生产 client 与内存 handler 验证全部七种意图、两种 forced choice 和本地限值跳过 Models GET；`ProviderModelMaximumTests` 覆盖重复请求及未知后缀不命中。fixture 只能证明请求投影与本地校验，不能证明在线模型接受或真实输出。

目录不描述价格、context window、采样或 thinking replay 权限。模型更换、账号和历史前缀对原生 thinking blocks 的限制仍由服务端判断，不能将 HTTP 接受推定为服务端实际使用了旧 reasoning。
