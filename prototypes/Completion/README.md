# Completion transport-liveness contract

`Completion`只判断能够从HTTP/SSE链路和provider协议中直接观察到的事实，不猜测LLM是否仍在工作。
一次streaming调用没有elapsed-operation timeout，也没有stream-idle timeout；不可见reasoning、排队或长时间
没有SSE frame都不是失败证据。`HttpClient.Timeout`统一为`Timeout.InfiniteTimeSpan`，调用方只能通过自己传入的
`CancellationToken`取消。

## 共享边界

- 成功HTTP响应必须声明`text/event-stream`；状态码、建连、读取和解码错误按原始transport/protocol错误传播。
- 所有Provider共享`CompletionSseEventReader`：按SSE空行提交frame，支持CR/LF/CRLF、多行`data:`、注释、
  UTF-8 BOM与replacement decoding；EOF时未提交的半个frame不会交给parser。
- 只有下表中的provider terminal才能确定远端结果。terminal到达后立即返回，不等待连接EOF。
- 已收到合法frame但在terminal前EOF，抛出`CompletionStreamInterruptedException`。这表示远端结果不确定，
  runtime不得透明重试或把它伪装为LLM拒答。
- caller cancellation保持原`CancellationToken`；observer cleanup失败不得覆盖原始read/cancellation异常。
- 未被当前版本识别、但外层event envelope合法的字段或事件保持forward-compatible；已知事件缺少必需shape、
  生命周期乱序或event/data类型冲突则fail closed。
- 如果TCP/HTTP连接进入silent half-open且系统没有报告断开，调用会无限等待。这是刻意选择：没有独立可靠的
  transport证据时，不用定时器推断LLM失败。

## Provider terminal matrix

| Provider surface | 权威成功/不完整terminal | 权威provider失败 | 非terminal或特殊规则 |
|---|---|---|---|
| OpenAI Chat Completions | 单一choice的非空`finish_reason`；`stop`/`tool_calls`为Completed，其他值为Incomplete | 顶层`error` | `[DONE]`只是传输哨兵；若它先于`finish_reason`到达，结果仍不确定。当前明确拒绝`n > 1`与多choice stream |
| OpenAI Responses | `response.completed`、`response.incomplete` | `response.failed`、`error` | `event:`与JSON `type`必须一致；`[DONE]`不能替代Responses terminal event |
| Anthropic Messages | `message_stop`，并要求完整的`message_start -> content blocks -> message_delta -> message_stop`生命周期 | `error` | `ping`与合法unknown named event只表示收到frame，不代表成功或失败；Anthropic没有`[DONE]` |
| Gemini `streamGenerateContent` | `candidate.finishReason`；`STOP`为Completed，其他值为Incomplete；无candidate时`promptFeedback.blockReason`为Incomplete | 顶层`error` envelope | Gemini没有文档化的`[DONE]`、named terminal或heartbeat；`responseId`只用于关联，不是resume cursor |

## 规格依据

- SSE framing：[WHATWG Server-sent events](https://html.spec.whatwg.org/dev/server-sent-events.html#event-stream-interpretation)
- OpenAI Responses events：[OpenAI API reference](https://platform.openai.com/docs/api-reference/responses-streaming)
- Anthropic event types and lifecycle：[Anthropic streaming Messages](https://platform.claude.com/docs/en/build-with-claude/streaming#event-types)
- Gemini response and finish reasons：[Gemini `generateContent` API](https://ai.google.dev/api/generate-content)

