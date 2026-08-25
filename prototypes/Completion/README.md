# Completion transport-liveness contract

`Completion`只判断能够从HTTP/SSE链路和provider协议中直接观察到的事实，不猜测LLM是否仍在工作。
一次streaming调用没有elapsed-operation timeout，也没有stream-idle timeout；不可见reasoning、排队或长时间
没有SSE frame都不是失败证据。`HttpClient.Timeout`统一为`Timeout.InfiniteTimeSpan`，调用方只能通过自己传入的
`CancellationToken`取消。

## 共享边界

- 成功HTTP响应必须声明`text/event-stream`；状态码、建连、读取和解码错误按原始transport/protocol错误传播。
- 所有Provider共享`CompletionSseEventReader`：按SSE空行提交frame，支持CR/LF/CRLF、多行`data:`、注释、
  UTF-8 BOM与replacement decoding；EOF时未提交的半个frame不会交给parser。
- 只有下表中的provider terminal evidence才能确定远端结果。显式terminal到达后立即返回，不等待连接EOF。
- 已收到合法frame但在terminal evidence前EOF，抛出`CompletionStreamInterruptedException`。这表示远端结果不确定，
  runtime不得透明重试或把它伪装为LLM拒答。唯一的窄兼容例外是Anthropic：所有content block均已关闭、
  已收到非空`message_delta.stop_reason`、随后无pending frame的clean EOF但缺少data-free `message_stop`时，按该stop reason结束。
- caller cancellation保持原`CancellationToken`；observer cleanup失败不得覆盖原始read/cancellation异常。
- 未被当前版本识别、但外层event envelope合法的字段或事件保持forward-compatible；已知事件缺少必需shape、
  生命周期乱序或event/data类型冲突则fail closed。
- 如果TCP/HTTP连接进入silent half-open且系统没有报告断开，调用会无限等待。这是刻意选择：没有独立可靠的
  transport证据时，不用定时器推断LLM失败。

## Provider terminal matrix

| Provider surface | 权威成功/不完整terminal | 权威provider失败 | 非terminal或特殊规则 |
|---|---|---|---|
| OpenAI Chat Completions | 单一choice的非空`finish_reason`；`stop`/`tool_calls`为Completed，其他值为Incomplete | 顶层`error` | `[DONE]`只是传输哨兵；若它先于`finish_reason`到达，结果仍不确定。当前明确拒绝`n > 1`与多choice stream |
| OpenAI Responses | `response.completed`、`response.incomplete`；若已观察到typed refusal，两者均收口为`Incomplete(response.refusal)` | `response.failed`、`error` | `response.refusal.delta/done`与message refusal content不是terminal；`event:`与JSON `type`必须一致；`[DONE]`不能替代Responses terminal event |
| Anthropic Messages | 首选`message_stop`，并要求`message_start -> content blocks -> message_delta(stop_reason)`；兼容缺尾帧relay时，blocks全关且已有非空`stop_reason`后的无pending-frame clean EOF也是降级terminal evidence | `error` | `ping`与合法unknown named event只表示收到frame，不代表成功或失败；Anthropic没有`[DONE]`；read failure/cancellation/protocol error绝不走clean-EOF兼容 |
| Gemini `streamGenerateContent` | `candidate.finishReason`；`STOP`为Completed，其他值为Incomplete；无candidate时`promptFeedback.blockReason`为Incomplete | 顶层`error` envelope | Gemini没有文档化的`[DONE]`、named terminal或heartbeat；`responseId`只用于关联，不是resume cursor |

OpenAI Responses refusal 只按typed wire evidence识别，不从普通正文猜测。`response.refusal.delta/done`、
`response.output_item.done` 的 message refusal content，以及terminal `response.output` fallback 使用
`(item_id, content_index)`协调；streamed prefix只补final suffix，重复final不重复输出，冲突或final后delta抛protocol
exception且不在异常中带refusal正文。同一时刻只允许一个未finalized key；message/output已知容器若缺失array shape、
entry不是object或缺少string `type`也fail closed，而合法unknown string type仍forward-compatible。正文可以作为
transient `ActionMessage.Text` / observer delta返回，但不会进入
`Errors`、termination reason/detail，也不会被SessionJournal持久化为成功`AgentActionProduced`。只有最终权威response
terminal到达后才产生non-success result；terminal前EOF、transport failure或cancellation仍是outcome uncertain，
`response.failed` / `error`仍覆盖为`Failed`。没有typed refusal witness时，既有`response.incomplete`的
`content_filter`等reason保持原语义；refusal始终使用独立`response.refusal`，不复用`content_filter`名称。

## 规格依据

- SSE framing：[WHATWG Server-sent events](https://html.spec.whatwg.org/dev/server-sent-events.html#event-stream-interpretation)
- OpenAI Responses events：[OpenAI API reference](https://platform.openai.com/docs/api-reference/responses-streaming)
- Anthropic event types and lifecycle：[Anthropic streaming Messages](https://platform.claude.com/docs/en/build-with-claude/streaming#event-types)
- Gemini response and finish reasons：[Gemini `generateContent` API](https://ai.google.dev/api/generate-content)

## ChatGPT Codex subscription client（Linux MVP）

`OpenAICodexResponsesClient` 可以直接作为 `ICompletionClient` 使用，不需要启动 local proxy。它只借用 Codex CLI
file-backed `auth.json` 的当前 access-token snapshot；Codex CLI 仍是唯一 login/refresh/write-back owner。

```csharp
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

var credentials = new CodexCliAuthFileCredentialProvider();
CodexSubscriptionCredential firstSnapshot =
    await credentials.GetCredentialAsync(ct);

using var client = new OpenAICodexResponsesClient(
    credentials,
    new OpenAICodexResponsesClientOptions {
        ExpectedAccountFingerprint = firstSnapshot.AccountFingerprint,
        Originator = "atelia", // 构造期可配；默认值也是 atelia
        MaxConcurrentRequests = 3
    }
);

CompletionResult result = await client.StreamCompletionAsync(
    request,
    observer: null,
    ct
);
```

运行边界：

- 当前只支持 Linux file-backed credential；`auth.json` 必须由当前用户拥有，mode 为 `0400` 或 `0600`；
- provider 每个 logical attempt 重读 snapshot，但从不 materialize refresh/id token，不 refresh、不写文件；
- access token 过期或 backend 401 且文件 generation 未变化时，先运行 Codex 让它 refresh，必要时重新 `codex login`；
- endpoint 固定为 `https://chatgpt.com/backend-api/codex/responses`，它不是公开稳定 API；
- `originator` 必须诚实稳定，允许构造时覆盖，不要伪装 `codex_cli_rs`、Pi 或 OpenCode；
- public OpenAI Responses 与 Codex Responses 使用不同 `ApiSpecId`，两边的 provider-native reasoning payload 不能交叉 replay。

Galatea 接入、connection shape、安全 preflight、环境变量和 live smoke 见
[`docs/Completion/openai-codex-subscription-client-design.md`](../../docs/Completion/openai-codex-subscription-client-design.md)。
