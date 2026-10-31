# 可读 reasoning 诊断 API

状态：2026-10-31 实施，尚未公开发布。本文描述当前源码；已发布包以其固定 tag 为准。

## 目标与入口

应用层通过 provider 无关的运行选项请求可读 reasoning，用于日志或展示：明文直接提供，不透明 reasoning 有服务端摘要时提供摘要。

```csharp
var options = new CompletionInvocationOptions {
    RequestReasoningText = true // 默认值；三参数调用也默认开启
};
var observer = new CompletionStreamObserver();
observer.ReceivedReasoningDelta += text => AppendDiagnosticText(text);
CompletionResult result = await client.StreamCompletionAsync(request, options, observer, ct);
foreach (var block in result.Message.Blocks.OfType<ActionBlock.ReasoningBlock>()) {
    RecordDiagnostic(block.Origin, block.PlainText, result.Termination);
}
```

`AppendDiagnosticText` / `RecordDiagnostic` 是宿主的日志方法。增量和最终快照是两个观察时机，宿主选择记录方式以避免重复。

选择 `CompletionInvocationOptions`，因为这是每次调用的诊断偏好，不是模型输入消息、工具合同或连接身份。不新增策略枚举或通用 capability 框架。连接 manifest、dispatch fingerprint、`CompletionDescriptor` 与原生 reasoning 的 Origin 不增加字段。

## 精确语义

- `RequestReasoningText` 默认为 true；内建客户端三参数与四参数默认入口一致。
- 尽力请求上游可读文本，与 `ReasoningEffort` 独立，不提高档位、不强制开启思考。
- false 停止额外请求，保留 provider/dialect 既有处理行为，不是输出过滤或删除原生回放负载。服务端仍返回的可读内容可以继续出现在结果和 observer 中。
- `PlainText` 可能是原文或服务端摘要，不保证完整推理过程；无可读内容为 null，不能据此判断是否思考。
- 库不解密、不自行生成摘要、不发起第二次模型调用。未知模型、不支持摘要的表面、安全删节以及模型自行决定不思考都允许没有文本。
- 密文和签名本身不作为文本事件发送。仅在最终块中出现的可读内容从 `PlainText` 读取，不凭空合成流式 delta。
- 第三方 `ICompletionClient` 默认接口回落仅沿用三参数方法，不保证主动索取摘要。非默认 cache hint 的既有 fail-fast 行为保持。
- observer 抛错、取消、中断和非成功终止合同继续适用；收到摘要不是调用成功证据。
- 不根据模型名猜测摘要支持、不因服务端拒绝自动重试或重发降级请求。不支持相关配置时走正常 HTTP failure；宿主可显式关闭此偏好。

## 协议映射

| 表面 | 默认开启 | 显式关闭 |
| --- | --- | --- |
| Responses / Codex，默认 effort | reasoning.summary:auto，省略 effort | 省略 reasoning 配置 |
| Responses / Codex，启用 effort | 原 effort 加 summary:auto | 仅原 effort |
| Responses / Codex，真正 Disabled | effort:none，无 summary | 相同 |
| Gemini | thinkingConfig.includeThoughts:true；默认 effort 不指定 thinkingLevel | 省略 includeThoughts，保留 effort 投影 |
| Anthropic，显式启用思考 | adaptive 加 display:summarized | 不请求 summarized，沿用服务端显示默认值 |
| Anthropic，默认已启用 adaptive 的已知模型 | adaptive/summarized，不指定 output effort | 省略额外显示请求 |
| Anthropic，默认未启用或未知 | 不为诊断强制开启思考 | 相同 |
| Chat / DeepSeek | 接收 reasoning_content；Ignore 方言提升到 CaptureOnly | 保留方言既有行为 |

Chat 的 CaptureOnly 不扩大 replay 权限；仅 ReplayCompatible 方言继续回放 reasoning_content。Anthropic 的 redacted_thinking 仍为 PlainText=null。

Anthropic 的 thinking.type 不能只为显示偏好而省略。`CompletionModelSpec.AdaptiveThinkingEnabledByDefault` 独立描述已知默认行为，不从 effort mapper 推断。内建已核实的 Opus/Sonnet/Haiku 5.5 登记为 true；未知默认不推断。自定义服务可以在 model spec 中显式提供该事实。

## Gemini 正文与回放

只有 thought:true 的 text 是诊断摘要，进入 reasoning delta；其余 text 进入正文。保留 thought 的 true/false/缺省区别、顺序、签名与工具调用。

继续使用一个整 turn 的 `GeminiReplayBlock`。PlainText 由 thought 文本确定性派生，codec 验证该视图与负载一致；投影分别校验正文和工具调用与原生负载一致，正文校验只比较非 thought text。摘要段触发 thinking begin/end，切到正文/工具/terminal 时结束观察，不为生命周期事件追加不完整回放块。失败清理关闭观察状态，不发布半成品 replay block。

## 日志与验收

调用日志 v12 在 invocationOptions 中记录 requestReasoningText 与 cache hint。结果可读视图仍使用 plainTextForDebug，原生负载继续无损保存；它不受库内 Debug 调用裁剪影响。应用只需可读日志时可仅消费 PlainText。

验收覆盖默认/显式开启/关闭与 effort 的请求矩阵；Gemini 正文隔离、流式事件、签名、持久化、原生回放与失败清理；关闭偏好后服务端返回的可读内容；日志透传。离线 fixture 与真实模型分别报告。独立 NuGet 消费者以唯一开发版本验证 public API 和实际 nupkg。

真实调用入口是 `experiments/ToolCallIdMatrix --reasoning-text <config>`。复用运行时凭据和宿主期限，只记录状态、计数、答案校验与协议字段白名单，不保存原始 reasoning 或认证信息。每端点最多四次调用，无自动重试。具体运行证据在完成验收后补充。

## 协议依据

- [OpenAI reasoning summaries](https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries)：显式请求摘要。
- [Anthropic thinking display](https://platform.claude.com/docs/en/build-with-claude/thinking#controlling-thinking-display)：显示配置与模型默认行为。
- [Gemini Generate Content thinking](https://ai.google.dev/gemini-api/docs/generate-content/thinking#thought-summaries)：includeThoughts 与档位分离。
- [Gemini Generate Content API](https://ai.google.dev/api/generate-content#ThinkingConfig)：thought、签名与可用时提供摘要的合同。
