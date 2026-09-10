# Responses 跨模型 native reasoning 生产实现与验收

本轮承接 [f88e982d 实验](2026-09-10-codex-reasoning-replay.md)，不是重新扩展到 reasoning 可见性或质量评测。
现行规则的单一维护点是 [adapter 合同 §6.4](../openai-codex-subscription-client-design.md#64-独立-protocol-identity)。

## 实施工作包

1. 共享 converter 仅取消 Model 相等条件；精确 ProviderId/ApiSpecId、原生载荷校验、工具邻接与原始 Origin 保留。
2. Responses-only RequestAdapterFingerprint 增加投影版本，ApiSpecId/codec 不变；旧 Prepared/Started 不允许静默改绑。
3. 探针从测试中替换 input 改为只读检查生产 wire，并补离线测试证明省略/改写 native item 时不会被探针“修好”。
4. 独立 review，Completion/Galatea 回归、Codex live 验收与升级文档。

没有新增模型白名单、连接开关、旧投影兼容分支或 reasoning.context 配置；未修改任何真实 Galatea 会话。
公共 Responses 与 Codex 共享实现为用户明确授权的假设，不调用付费公共 API，也不将此假设写成已实测。

## 当前 live 验收方法

[测试入口](../../../tests/Completion.Tests/OpenAI/OpenAICodexReasoningReplayLiveTests.cs)仍使用显式 opt-in gate。
生产 client、converter、credential provider、HTTP 与 SSE parser 均在主链中；handler 不替换 input、不传入 null target、
不改写 request body。仅检查最终 wire reasoning item 与历史原生 JSON 的完整值相等，并收集脱敏 terminal metadata。
缺失 reasoning 时 before-send 失败，不能得到虚假的通过结果。

默认最多 17 次串行发送（3 seeds + 7 方向 × 2 阶段），每次最多一次发送，不自动重试、不删 reasoning 重发。
四个跨模型方向是 Sol↔Astra、Sol↔Luna，另有三个同模型对照；覆盖工具结果后续接与下一用户回合。
不设置 reasoning.context；只记录 backend 的有效模式，不推断 reasoning 是否被利用。

```bash
ATELIA_RUN_CODEX_REASONING_REPLAY_LIVE=1 \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/absolute/path/to/auth.json \
ATELIA_CODEX_REASONING_REPLAY_REPORT=/absolute/path/to/new-report.jsonl \
ATELIA_DEBUG_FILE_LEVEL=Error ATELIA_DEBUG_CONSOLE_LEVEL=Error \
dotnet test tests/Completion.Tests/Completion.Tests.csproj --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~LiveE2E_NativeReasoning_ModelSwitchMatrix'
```

报告路径必须为不存在的新文件，父目录已存在，Unix `0600`；不保存 opaque payload、凭据或账号标识。
`ATELIA_CODEX_REASONING_REPLAY_SCOPE=astra-control` 只生成独立 Astra 对照样本（最多 3 次），不是重试旧请求。
无凭据离线探针 filter 为 `FullyQualifiedName~Probe_`；完整非 live Completion 回归使用
`FullyQualifiedName!~LiveTests|FullyQualifiedName~Probe_`。

## 部署注意

投影指纹变更不是历史数据迁移。Idle 会话中的合法 v2 native reasoning 可直接读取；旧 frozen work 必须用匹配的
旧 adapter 完成之后再升级。即使用户授权 Restart，新版本也不会换用新投影重发旧请求。
没有在新版自动 abandon、改 manifest 或改 Origin 的路径。详见 [Galatea 升级说明](../../Galatea/runtime.md#模型切换与-reasoning-回放排障)。

## 2026-09-10 现场验收结果

[逐调用脱敏报告](2026-09-10-codex-reasoning-production.jsonl)记录 **17/17 成功**，约 46 秒完成整个矩阵。
3 个源模型均返回非空 encrypted reasoning 与 checkpoint 工具调用；14 次 continuation 均保持完整 native item 值相等。
全部请求只发送一次，HTTP 200、response.completed/status=completed、生产 parser Completed，响应模型与目标一致。

| 来源 → 目标 | 工具结果后续接 | 下一用户回合 |
|---|---|---|
| Sol → Astra | 成功 | 成功 |
| Astra → Sol | 成功 | 成功 |
| Sol → Luna | 成功 | 成功 |
| Luna → Sol | 成功 | 成功 |
| Sol → Sol | 成功 | 成功 |
| Astra → Astra | 成功 | 成功 |
| Luna → Luna | 成功 | 成功 |

所有成功响应报告 reasoning.context=all_turns，请求未设置该字段。不以此推导旧 reasoning 被实际利用。
公共 API 未调用；真实 Galatea 会话未启动、未迁移。当前 probe 已不含历史实验的 input 替换路径。

离线回归：Debug/Release 各通过 Completion 795 项、Galatea 827 项（排除 LiveTests，另显式包含 Probe_ 的 3 项
无凭据检查）；Node 13 项通过；Galatea Release build 零警告零错误。scoped docs checker 26 文件零诊断，
新增 Completion 文档本地链接另行检查，`git diff --check` 通过。两个实现子任务由独立 reviewer 复核，文档 findings 已收尾。
