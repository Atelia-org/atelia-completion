# GPT-6 Astra → GPT-6 Sol 旧会话续轮验证（2026-09-24）

## 范围与判据

本次检查 `OpenAICodexResponsesClient` 的 ChatGPT Codex Responses 路径。
不读取或修改真实 Galatea 会话；使用同一 Codex auth 文件构造独立的三次调用。
公共 `OpenAIResponsesClient` 与 Codex 使用同一消息转换器，但不同 `ApiSpecId`；
本次只对公共 profile 增补离线投影断言，没有公共 API 在线调用。

[OpenAI 模型指南](https://developers.openai.com/api/docs/guides/latest-model)
将 Astra 与 Sol 列为 GPT-6 家族；[reasoning 指南](https://developers.openai.com/api/docs/guides/reasoning)
说明同家族持久化 reasoning 可复用。据此推断 Astra → Sol 属于兼容方向，
仍需在线验证。`all_turns` 会将可用的兼容 reasoning
纳入下一次 sample；加密内容不向客户端公开。因此在线判据分三层：

1. 本地转换器允许相同 ProviderId/ApiSpecId 下的 Astra → Sol 原生 reasoning，
   且保留历史块的 `Origin` 与完整 native item 值。
2. 实际 Codex backend 接受携带原生 item 的 Sol 请求，返回 HTTP 200、
   `response.completed` 和 parser `Completed`，响应模型为 Sol，报告有效
   `reasoning.context=all_turns`。
3. 首个 Sol 续轮能读取并使用旧会话的**可见**工具结果：准确复述只在该
   工具结果中出现的收据标记；下一用户回合继续正确复述。此项不等同于
   证明它内部使用了加密 reasoning。

## 实现路径与 Origin

`OpenAIResponsesMessageConverter.HasReplayCompatibleOrigin` 只比较
`ProviderId` 与 `ApiSpecId`，不比较模型 ID；同 profile 跨模型时，
`OpenAIResponsesReasoningBlock` 的原始 `Origin.Model` 保留为 `gpt-6-astra`。
不相同 provider/profile 的 reasoning 会从请求投影中省略；畸形或伪造的
同 profile 原生载荷仍会在发送前被拒绝。离线
`ModelProvenance_DoesNotFilterOrRewriteNativeReasoning` 新增了 Astra → Sol
在 Codex 与公共 Responses 两个 profile 的精确行，并通过持久化序列化往返
与最终 JSON 值相等检查。

在线复用既有 `OpenAICodexReasoningReplayLiveTests` 的只读 wire 探针，新增
`astra-to-gpt6-sol` 定向范围：

1. Astra 根据合成算术任务生成非空 `encrypted_content` 的 reasoning 和
   `checkpoint` 工具调用。
2. 以 Sol 接续 Astra 的 Action 和测试内工具结果；工具不产生外部副作用，
   结果中注入仅该处出现的收据标记，要求 Sol 精确返回。
3. 再追加 Sol 的完成 Action 和新用户消息，仍要求返回该标记。

探针不替换 request input，不改历史 `Origin`；每次请求最多放行一次，
比较最终 wire 中原生 item 与历史 `RawItemJson` 的完整 JSON 值。报告仅含
模型、阶段、HTTP/terminal、数量、布尔值与耗时，不含凭据、收据内容、
原生 payload 或账号标识。每个调用使用生产 client、credential provider、
HTTP 和 SSE parser。

## 本机实测

来源基线 `df5c313`；Linux、.NET SDK `10.0.201`；
`/root/.codex/auth.json` 在运行时只读。
本次定向复跑三次调用，均单次发送成功：

| 阶段 | HTTP / 完成 | 原生 reasoning item | 值原样 | 有效 context | 可见标记 |
| --- | --- | ---: | --- | --- | --- |
| Astra seed | 200 / Completed | 0 | 是 | `all_turns` | 产生加密 reasoning 与工具调用 |
| Astra → Sol 工具结果续轮 | 200 / Completed | 1 | 是 | `all_turns` | 精确回复 |
| Astra → Sol 下一用户回合 | 200 / Completed | 2 | 是 | `all_turns` | 精确回复 |

结论：在此账号与时间点，**合成 Astra 旧会话可经生产 Codex client 由
GPT-6 Sol 顺利接续；Sol 使用了旧会话的可见工具结果，Origin 不会仅因
模型切换阻挡原生 reasoning**。`all_turns` 与原生 item 到达服务端，是
reasoning 可用性的协议证据；加密 reasoning 是否实际影响模型内部决策
无法从客户端观察，不能把可见标记验证等同于这一点。

本轮没有运行真实 Galatea 旧会话、修改其冻结请求或做公共 Responses
在线测试。旧 Prepared/Started 请求的连接和模型仍须按其冻结身份恢复；
这里验证的是允许选择新模型的**后续新请求**，并不授权重绑旧请求。

## 复现

报告目标须是不存在的新文件，父目录已存在；在 Unix 上以 `0600` 创建。
未设置 opt-in 时测试体提前返回，不构成在线成功。

```bash
ATELIA_RUN_CODEX_REASONING_REPLAY_LIVE=1 \
ATELIA_CODEX_REASONING_REPLAY_SCOPE=astra-to-gpt6-sol \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/absolute/path/to/auth.json \
ATELIA_CODEX_REASONING_REPLAY_REPORT=/absolute/path/to/new-report.jsonl \
ATELIA_DEBUG_FILE_LEVEL=Error ATELIA_DEBUG_CONSOLE_LEVEL=Error \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~LiveE2E_NativeReasoning_ModelSwitchMatrix'
```

受影响离线用例 25/25 通过；完整离线 Completion.Tests 860 通过、
1 个 Windows 限定测试在 Linux 跳过。生产库代码未改。
