# claude-haiku-5-5 模型规格

- 核查日期：2026-10-11（Asia/Singapore）
- 适用表面：Claude API 标准 Messages streaming；仅登记精确 `claude-haiku-5-5`
- 证据类型：官方文档与仓内离线 HTTP/SSE fixture；未进行真实模型调用

## 官方事实

[模型页](https://platform.claude.com/docs/en/models/haiku-5-5/overview)列出 Claude API ID `claude-haiku-5-5`，发布日期 2026-10-07，标准输出上限 128,000 token；Batch API beta 的 300K 上限不适用于本条目。

[Effort 文档](https://platform.claude.com/docs/en/build-with-claude/effort#recommended-effort-levels-for-claude-haiku-55)确认 `output_config.effort` 支持 `low/medium/high/xhigh/max`，默认 `medium`。`thinking: {"type":"disabled"}` 仅在 `low/medium/high` 时受支持；与 `xhigh/max` 同时发送会返回 400。

[迁移指南](https://platform.claude.com/docs/en/models/haiku-5-5/migration-guide#configure-thinking)确认默认 adaptive thinking，默认省略可读摘要，可用 `adaptive` 与 `display: "summarized"` 请求摘要。强制 `tool_choice`（`any` 或具名 `tool`）受支持；这类响应直接开始工具调用，不返回 thinking block。

## 本库登记与政策

`BuiltinModelSpecs.AnthropicMessages` 登记 `OutputTokenLimit = 128_000`、五个启用挡位、`supportsDisabled: true` 以及 `ForcedToolChoiceSupported = true`。不沿用 Opus/Sonnet 5.5 的禁止关闭与 forced-tool 禁令。

| 业务意图 | 本库 wire 投影 |
| --- | --- |
| ProviderDefault | 省略 thinking 与 output_config，保留服务端默认 medium |
| Disabled | thinking.type=disabled；省略 display 与 output_config，默认 medium 满足关闭条件 |
| Low / Medium / High / XHigh / Max | adaptive + summarized，effort 分别为 low / medium / high / xhigh / max |

Disabled 是独立业务意图，不与显式 XHigh/Max 混用；现有 adapter 无需修改即可构造有效的关闭组合。RequiredAny/RequiredNamed 保持原始 `any/tool` 要求；支持强制工具不保证响应包含 reasoning。

请求原样发送精确 ID，不登记猜测别名、日期后缀、平台前缀或同族通配规则；宿主网关的不同名字需要显式扩展目录。

## 验证边界

`AnthropicModelSpecsTests` 用生产 client 与内存 handler 验证全部七种意图、两种 forced choice 和本地限值跳过 Models GET；Disabled 专门检查没有遗留 output_config 或 display。`ProviderModelMaximumTests` 覆盖重复请求及未知后缀不命中。未做真实服务调用，也未新增 live opt-in 探针。

目录不描述价格、context window、采样或 thinking replay 权限。服务端按模型与账号决定能否读取原生 thinking blocks；本次不改变 Origin、协议身份或回放校验。
