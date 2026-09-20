# Diagnostics 评审遗留待处理问题

状态：已记录、已裁决延后处理（2026-09-21）。来源：DebugUtil 重设计辩证评审（见 [debug-util-redesign.md](debug-util-redesign.md)）。处理前需按传输合同单独设计与测试；本文件不构成实施授权。

## 1. 六处 provider fail-closed 变更（Mill/Bacon 提议，已裁决延后）

共同理由：当前"打 Warning 后继续成功"会把语义可疑的输入静默改写成看似合法的请求；`RawToolCall` 的核心事实就是 provider 原始 JSON 参数。注意风险：**fail closed 会改变历史回灌兼容性**——已持久化的非对象/非法 raw arguments 在下一轮投影时将从"回退 `{}`"变为抛异常，需评估既有数据迁移与测试矩阵。

### 1.1 replay raw arguments 回退空对象（4 处）

| 位置 | 现状 | 提议 |
|---|---|---|
| [AnthropicMessageConverter.cs:431](../../src/Completion/Anthropic/AnthropicMessageConverter.cs) | 非对象 raw arguments → Warning + 回退 `{}` | 抛协议异常，请求构造失败 |
| [AnthropicMessageConverter.cs:437](../../src/Completion/Anthropic/AnthropicMessageConverter.cs) | 非法 JSON raw arguments → Warning + 回退 `{}` | 同上 |
| [GeminiMessageConverter.cs:345](../../src/Completion/Gemini/GeminiMessageConverter.cs) | 非对象 → Warning + 回退 `{}` | 同上 |
| [GeminiMessageConverter.cs:351](../../src/Completion/Gemini/GeminiMessageConverter.cs) | 非法 JSON → Warning + 回退 `{}` | 同上 |

参考：Tools 执行路径对非对象 JSON 已返回 Failed；provider 投影路径应对齐 fail-closed 语义。

### 1.2 OpenAI Responses reasoning 投影异常（2 处）

| 位置 | 现状 | 提议 |
|---|---|---|
| [OpenAIResponsesStreamParser.cs:590](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs) | reasoning item 未完成即切换 → Warning 后继续 | 抛协议异常（或按合同评估 MarkIncomplete fail-closed） |
| [OpenAIResponsesStreamParser.cs:632](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs) | reasoning item done mismatch → Warning 后继续收口 | 同上 |

注意：另一处 reasoning 相关调用点（`OpenAIResponsesStreamParser.cs:259` unfinished reasoning at terminal）已按权威结果携带事实降为 Debug，不属于本项。

## 2. clean-EOF 事实写入 CompletionTermination.Detail（Bacon 提议，已裁决延后）

位置：[AnthropicClient.cs:215](../../src/Completion/Anthropic/AnthropicClient.cs)。

现状：Anthropic clean-EOF 兼容路径（blocks 全关 + 非空 stop_reason + clean EOF 缺 `message_stop`）收口为正常 terminal，但结构化结果不携带"经由降级 terminal evidence 收口"这一事实，仅打 Warning。

提议：`CompletionTermination` 的 `Completed/Incomplete/Failed` 工厂均已支持 `detail` 参数（见 [CompletionTermination.cs](../../src/Completion.Abstractions/CompletionTermination.cs)）；将降级收口事实写入 `CompletionTermination.Detail`（如 `terminalSource=clean-eof-stop-reason`），随后把该 Warning 降为 Debug。

设计问题：需确认 Completed termination 携带 evidence detail 不与"只有 Completed 正文适合成功业务路径"边界冲突——detail 是证据描述，不是正文，不应进入成功业务分支的判定。

## 3. 其他推迟项（触发条件）

| 项 | 触发条件 |
|---|---|
| 全局宿主编程式配置 / 进程内事件钩子 | 真实宿主（如 Galatea）提供具体配置/运维场景 |
| 逐测试捕获日志（替代 `TestHostDiagnostics` 进程级默认） | 出现依赖 console 内容的测试断言需求 |
| 跨进程文件锁 / rotation | 真实多进程审计需求（当前文件明示非审计回执） |
| 重建类别选择器（Debug-only 控制台筛选） | 真实源码联调需要按类别收窄控制台 Debug 输出的排障记录 |

## 4. 范围外（所有者自行处理）

Galatea 仓外对 `Print`/`Log`/`ClearLog`/`DebugEventKind` 的潜在调用确认由仓库所有者另行处理，不阻塞本仓 public 面收缩。
