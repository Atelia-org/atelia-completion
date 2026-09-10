# Codex backend 跨模型 native reasoning 回放实验

日期：2026-09-10。生产代码基线：`116b96f7`。本次仅添加探针与实验记录，不改变生产 replay policy。

> 历史记录：本页方法对应实验提交 `f88e982d`，下文“当前/本次”均指该实验时点。
> 探针后来已升级为不替换 input 的生产 client 验收；新实现、验证结果和当前重现方法见
> [生产跨模型回放验收](2026-09-10-codex-reasoning-production.md)。本页原始结果不作追溯改写。

## 结论与范围

`gpt-5.6-sol ↔ gpt-6-astra`、`gpt-5.6-sol ↔ gpt-5.6-luna` 的四个方向，均在携带原生
reasoning item 的情况下获得 HTTP 200、`response.completed/status=completed`，并通过生产 SSE parser
返回 `CompletionTerminationKind.Completed`。response.model 均与请求的目标模型一致。

这是当前单账号、小型合成上下文的 **请求接受与完整完成证据**，不是全型号兼容承诺、长期可靠性证明，
也不证明服务端将旧 reasoning 渲染给模型或模型利用了它。本轮不测试公共 Responses、跨账号、其他厂商、
旧 API profile、损坏 payload、过期 payload、长历史或 `reasoning.context` 的配置映射。

## 方法

探针：[OpenAICodexReasoningReplayLiveTests](../../../tests/Completion.Tests/OpenAI/OpenAICodexReasoningReplayLiveTests.cs)。
逐调用脱敏证据：[JSONL](2026-09-10-codex-reasoning-replay.jsonl)。

- 使用同一个显式指定的 Codex CLI auth file，生产 credential provider、HTTP transport 与 SSE parser。
  只读凭据，不复制或输出 token、账号标识或 credential fingerprint，不访问 Galatea session/config。
- 三个模型各生成一个合成算术任务的 `checkpoint` 工具调用，必须含非空 `encrypted_content`，才成为可用 seed。
- 对每个 source → target，先发送原任务、seed Action 和工具结果；再追加 target 的完成 Action 与一条新用户消息。
  两阶段都继续携带 source reasoning。工具仅为测试内返回固定文本，不执行外部操作。
- `store=false`、`stream=true`、`reasoning.effort=medium`、`summary=auto`；不设置 `reasoning.context` 或 output token cap。
- 测试专用 handler 将 `input` 替换成现有 converter 的 `targetInvocation:null` 投影。此路径仍执行
  exact Codex API profile 和 native payload 校验，仅绕过模型身份相等判断。**生产 client 的默认过滤仍存在。**
- 不改写中立历史的 `Origin`，不把 summary/PlainText 伪装成 reasoning。发送前从最终序列化请求中取出每个
  reasoning item，与原始 `RawItemJson` 做完整 JSON 值相等检查，包括 opaque 字符串、id、summary 和扩展字段。
  JSON 排版/转义字节可以不同；`encrypted_content` 字符串值不变。
- handler 离线测试检查仅替换 input、保留其他 body 字段和响应字节、保留缺省 Content-Type、禁止第二次发送。
  每个调用最多放行一次 HTTP 请求，不做自动重试或删 reasoning 后重发。
- 原生 payload 仅保留在测试进程内存；持久化报告只包含模型名、阶段、时间、计数、相等检查和完成状态。

## 实测结果

每格为一次独立 continuation；不是统计成功率估计。

| 来源 → 目标 | 工具结果后续接 | 下一用户回合 |
|---|---|---|
| Sol → Astra | 200 / Completed | 200 / Completed |
| Astra → Sol | 200 / Completed | 200 / Completed |
| Sol → Luna | 200 / Completed | 200 / Completed |
| Luna → Sol | 200 / Completed | 200 / Completed |
| Sol → Sol（对照） | 200 / Completed | 200 / Completed |
| Luna → Luna（对照） | 200 / Completed | 200 / Completed |
| Astra → Astra（补充对照） | 200 / Completed | 200 / Completed |

所有成功 continuation 的 `NativeItemsUnchanged=true`，包含 1–2 个完整原生 reasoning item。
所有获得成功 terminal 的响应均报告 `reasoning.context=all_turns`。这只是当前 backend 的有效模式观测，
不能由此推导每个旧 item 被渲染或利用。

完整执行账目共 **23 次 HTTP 发送尝试**，保留失败记录，不将失败补写成成功：

1. 首轮探针自身缺陷：3 次 seed 均收到 HTTP 200 与 completed event，但 handler 错给缺省 Content-Type
   补为 `application/octet-stream`，随后被生产 client 拒绝。修正 handler 并添加两个离线测试后重新生成样本。
2. 主矩阵：17 次，16 次完整成功。Astra → Astra 的 next-user-turn 对照发生
   `OpenAICodexResponsesException`，未取得 HTTP 响应。该轮未采集 typed Reason，具体原因未确认，
   不能归因于 reasoning 拒绝，也不能声称主矩阵 17/17 通过。四个跨模型方向的 8 次调用全部成功。
3. 新样本 Astra 对照：3 次，3/3 完整成功。重新生成 seed；没有恢复或重发前一轮结果不确定的请求。

## 重现历史实验（使用 f88e982d）

默认关闭；未设置 enable switch 时 test 返回，不代表 live 验收通过。显式开启后，默认完整矩阵最多调用 17 次，
`astra-control` 则最多 3 次。报告必须是**不存在的新文件**，父目录已存在；Unix 以 `0600` 创建。

```bash
ATELIA_RUN_CODEX_REASONING_REPLAY_LIVE=1 \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/absolute/path/to/auth.json \
ATELIA_CODEX_REASONING_REPLAY_REPORT=/absolute/path/to/new-report.jsonl \
ATELIA_DEBUG_FILE_LEVEL=Error ATELIA_DEBUG_CONSOLE_LEVEL=Error \
dotnet test tests/Completion.Tests/Completion.Tests.csproj --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~LiveE2E_NativeReasoning_ModelSwitchMatrix'
```

仅跑新的 Astra 对照时额外设置 `ATELIA_CODEX_REASONING_REPLAY_SCOPE=astra-control`。不提供该值时为 `matrix`。
无凭据的 handler 回归 filter：`FullyQualifiedName~Probe_InputReplacement`。

## 下一步的依据，不是本轮已实施的变更

当前实测支持取消 **Codex native reasoning 仅因 Model 不同而产生的拦截/过滤**；不支持取消结构校验、
混淆其他厂商的 native carrier、开放 public/Codex profile 互投或修改真实 Origin。

生产落地应由 Codex profile 控制 replay policy，保持中立历史来源信息不变。由于当前生产已将跨模型 reasoning
省略，改为发送会改变现有合法请求的 wire，须显式处理 adapter identity/version 与 frozen-request recovery；
不能复用上一轮“旧成功请求 wire 不变”的理由。`reasoning.context` 可后续作为 provider-specific connection
配置评估，本轮不实现、不引入统一全厂商开关。

相关官方背景：[Reasoning models](https://developers.openai.com/api/docs/guides/reasoning)。
公共 API 文档不替代本页记录的 Codex backend 现场证据。
