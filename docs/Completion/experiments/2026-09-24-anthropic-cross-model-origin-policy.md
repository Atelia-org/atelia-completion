# Anthropic 跨模型 reasoning Origin 策略（2026-09-24）

## 决策

用户明确要求：同一 Anthropic Messages provider 与 API profile 的原生
thinking 回放不由本地客户端猜测模型兼容性。模型 ID 不再参与 Origin 校验；
服务器负责判断签名与目标模型是否兼容，并通过响应接受、拒绝或转换该块。
这不是模型 ID 白名单；包括尚未认识的新 ID 和反向切换。

`AnthropicClient` 仍从自己和请求生成 `targetInvocation`，
`AnthropicMessageConverter.BuildThinkingBlock` 只要求历史块的 `ProviderId`
和 `ApiSpecId` 与目标一致。`Origin` 保持历史来源，不重写成目标模型。
客户端仍要求 `AnthropicReasoningBlock` 原生类型，解码其 authoritative
thinking 或 redacted thinking 载荷，并检查可显示的 `PlainText` 与载荷
一致。跨 provider/profile 的块和本地畸形载荷继续在 HTTP 前拒绝；
这属于客户端自身的协议与载荷边界，不是模型兼容性推断。

此策略与共享 OpenAI Responses converter 的跨模型 Origin 处理原则一致，
但 Anthropic 服务端对签名的实际处理仍以该次响应为准。本地接受投影
不保证服务器读取了块，也不把 HTTP 200 当作此证明。

## 验证

离线 `AnthropicMessageConverterTests` 覆盖：

- 4.6 → 5.5、5.5 → 4.6、未知 ID → 未知 ID 均可投影；
- thinking 和 redacted thinking 经 durable Action 序列化往返后，
  原始 Origin、载荷与签名保持；
- provider 或 API profile 不同仍拒绝；跨模型但 `PlainText` 伪造仍拒绝。

`AnthropicCrossModelThinkingLiveTests` 改用生产 `AnthropicClient` 完成两次
实际中转站调用，不再通过测试代码传 `targetInvocation: null` 或手工发送
Messages 请求。首次 4.6 生成一个带签名的 thinking 块；5.5 续轮在请求中
原样携带 thinking 文本和签名。两次均 HTTP 200、`Completed`，响应模型声明
与请求相符；5.5 回复正确数字，并有 `message_stop`。首轮 `system` 与
用户消息的前缀哈希在续轮保持相同。

本次中转站对 `thinking-binding-controls-2026-08-01` beta header 仍未返回
`input_transformations`。该缺失只作为 metadata 记录，不再决定客户端
能否发请求。历史强证据实验及其当时的更严格策略见
[4.6 → 5.5 中转站专项实验](2026-09-24-anthropic-opus46-to-opus55-thinking.md)。

测试使用 `CLAUDE_BASE_URL` 和 `CLAUDE_API_KEY` 的运行时值，不记录其内容；
只输出 HTTP、模型、块数、原样回放与终止布尔值。复现：

```bash
ATELIA_RUN_ANTHROPIC_46_TO_55_THINKING_LIVE=1 \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~AnthropicCrossModelThinkingLiveTests' \
  --logger 'console;verbosity=detailed'
```

未设置 opt-in 时测试体提前返回，不构成在线成功。真实 Galatea/cyber
会话、旧冻结请求与恢复状态均未修改；先前打出的 dev feed 包也未包含
本次实现。
