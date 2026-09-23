# Opus 4.6 thinking → Opus 5.5 续轮中转站专项实验（2026-09-24）

## 问题与验收标准

[Anthropic preserved thinking 文档](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking)
列明 Opus 5.5 可读取更早 Opus 模型的 thinking，包括 4.6 → 5.5 方向。
同页还规定 5.5 检查此前的 `system`、`tools` 与 `messages` 前缀；不兼容模型的
thinking 块可能被静默丢弃。启用 `thinking-binding-controls-2026-08-01` beta
header 后，`input_transformations` 可显示丢弃或前缀不匹配的原因。

当前 `AnthropicMessageConverter` 对 provider-native reasoning 的生产回灌要求
完整 `Origin == targetInvocation`，因此会在 HTTP 前拒绝跨模型续轮。这项实验
要先证明实际配置的 Messages 中转站能保留并供 5.5 读取 4.6 的块，再决定
是否只放宽这个精确方向。HTTP 200、正文正确或请求携带签名，都不足以单独
证明模型读取了块。

## 隔离实验

`AnthropicCrossModelThinkingLiveTests` 是显式 opt-in 测试。它从运行时环境读取
`CLAUDE_BASE_URL` 与 `CLAUDE_API_KEY`，不打印其值。第一次调用使用
`AnthropicClient` 让 `claude-opus-4-6` 产生 `Completed` 的正文和带签名的
`AnthropicReasoningBlock`。之后三次用相同转换器投影请求，但**只在实验中**
传 `targetInvocation: null` 绕过生产 Origin 检查：

1. 原样重建 `system` 与首轮 user 消息，向 5.5 回灌原 thinking、原正文并追加
   user 消息；核对请求中的 thinking 文本与签名和首次响应完全一致。
2. 在同一合成历史上只修改旧 `system`，检查 400 的前缀绑定错误或
   `input_transformations` 的 `prefix_binding_mismatch`。
3. 把首轮 thinking 从 assistant 消息移除，其余输入保持相同，比较响应的
   `input_tokens`，辅助观察是否计入旧块。

测试以哈希比较前缀，只输出 HTTP 状态、块数、长度、token 数及布尔诊断；
不保存完整请求、响应、签名或凭据，也不访问真实 Galatea/cyber 会话。
`LiveE2E` 与 opt-in 同时保护默认离线运行。复现命令：

```bash
ATELIA_RUN_ANTHROPIC_46_TO_55_THINKING_LIVE=1 \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~AnthropicCrossModelThinkingLiveTests' \
  --logger 'console;verbosity=detailed'
```

此测试要求强证据，故当前中转站的运行结果是预期的**失败**，不能当作
在线通过。未设置 opt-in 时测试体提前返回，也不能当作在线通过。

## 当前结果

环境：Linux、.NET SDK `10.0.201`，实际配置的 HTTPS Messages 中转站，
来源基线 `cc9d4ce`。最终复跑的四次 generation 摘要：

| 阶段 | HTTP | 观察 |
| --- | ---: | --- |
| 4.6 首轮 | 200 | `Completed`；1 个 thinking 块，文本 13 字符、payload 485 字节 |
| 5.5 原样续轮 | 200 | 请求确实带入同一 thinking 文本和签名；`message_stop`；稳定前缀哈希相同；响应未提供 `input_transformations`；`input_tokens=90` |
| 5.5 修改旧 system | 200 | 前缀哈希不同；无 400 或前缀不匹配诊断 |
| 5.5 去掉旧 thinking | 200 | 请求中无 thinking；`input_tokens=90` |

上述结果证明客户端可构造并发送精确回灌请求，5.5 续轮也能完成；**没有证明
中转站把 4.6 thinking 交给 5.5 读取**。相同的 `input_tokens` 提示旧块可能未
计费，但中转站计数语义未单独校验，不能据此断言它一定丢弃了块。
beta header 已由测试客户端发送；`input_transformations` 缺失可能来自中转站
不支持、未透传，或上游行为差异，现有证据无法定位是哪一跳。故意修改前缀
的请求也没有给出绑定诊断，所以此中转站暂不能用于验收新账号的 400 行为。

## 决策与后续条件

生产 `Origin == targetInvocation` 校验保持原样，未将 4.6 → 5.5 加入白名单；
之前打出的 dev feed 包也不包含此实验代码。若以后中转站支持该 beta 元数据，
或可用受控的官方 API 凭据重跑，应要求原样续轮 `input_transformations` 明确为空，
并要求故意修改旧前缀报告绑定不匹配，再加精确方向的离线测试并修改生产校验。
完成这一层之后，再在隔离的 Galatea 合成会话中验证其实际上下文重建和恢复
路径；真实 cyber 会话不参与实验。
