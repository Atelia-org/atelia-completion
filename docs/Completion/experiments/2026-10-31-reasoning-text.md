# 可读 reasoning API 验收

日期：2026-10-31。实现与在线调用来源 commit：`8759e3530c367925788ad15ccbe19a1f9ee8a12c`。环境：Windows，SDK 10.0.201，Release。合同见 [设计](../reasoning-text-design.md)。本次没有推送或公开发布。

## 离线与独立复核

按 CI 关闭全部 opt-in 开关和 OPENROUTER_API_KEY，排除 LiveE2E/LocalE2E。完整测试：1268 通过、0 失败、3 跳过。三项跳过均为 Linux 文件权限/owner 测试，未声称完成跨平台验收。TRX 在本地 `artifacts/reasoning-text-tests/reasoning-text-offline.trx`。

覆盖默认入口、开关/effort 矩阵、false 下仍接收上游文本、Gemini 正文隔离/生命周期/签名/回放/codec 校验及取消/断流/observer 异常清理。实验工具 28 项离线 self-test 通过。独立只读复核未发现阻塞代码问题，指出的两处文档歧义已修正。主线程另行核对源码、测试结果、在线白名单及包消费产物。

## 真实模型

运行：`dotnet run --project experiments/ToolCallIdMatrix -c Release --no-build --no-restore -- --reasoning-text experiments/ToolCallIdMatrix/config.local.json`。

运行 ID：`20261031T095012476Z-reasoning-text-5aeac7dc`。宿主期限 90 秒/次；预算 24，实际 16 次调用、16 个 POST、无其他 HTTP 请求。没有探针自动重试。

题目：求正整数 n 的最小值，满足模 17 余 5、模 19 余 7、模 23 余 11，仅返回整数。宿主穷举一个完整余数周期独立得到 7417。四阶段分别为默认 effort+三参数默认开启、默认 effort+false、Low+true、携带 Low 响应原样 Message 再检查答案。

| 模型/协议 | 默认开启摘要字符 | 默认关闭摘要字符 | Low 开启摘要字符 | 原生消息回放 | 调用与答案 |
| --- | --- | --- | --- | --- | --- |
| claude-haiku-5-5 / Anthropic Messages | 296 | 0 | 425 | Completed，源含 1 个 reasoning block；本轮无新增摘要 | 4/4 Completed 且答案正确 |
| gpt-6-luna / public Responses | 31 | 0 | 20 | Completed，源含 1 个 reasoning block；本轮摘要 29 字符 | 4/4 Completed 且答案正确 |
| gpt-6-luna / Codex Responses | 30 | 0 | 31 | Completed，源含 1 个 reasoning block；本轮摘要 25 字符 | 4/4 Completed 且答案正确 |
| gemini-3.8-flash / Generate Content | 380 | 0 | 275 | Completed，源含 1 个 reasoning block；本轮无新增摘要 | 4/4 Completed 且答案正确 |

八个首次开启样本全部实际取得可读内容，且 delta 与最终 PlainText 完全相等。加上两个有摘要的回放响应，共 10 个响应包含可读文本；其余 6 个没有可读文本，不据此推断思考是否发生。所有 thinking begin/end 平衡；正文只返回正确整数，因此没有摘要混入成功正文。

白名单确认：默认 Responses/Codex 发送 summary:auto 且省略 effort，关闭时不请求 summary；Low 保留 low。Gemini 默认只请求 includeThoughts、没有 thinkingLevel；Low 同时发送 low。Anthropic 开启时发送 adaptive/summarized，关闭时没有这两个显示控制。Anthropic output_config.effort 未包含在实验白名单中，其精确投影由离线请求 fixture 验证。

机器回执：[reasoning-text.json](2026-10-31-reasoning-text.json)。按仓库规则统一 LF 后的归档 SHA256：`63f64eb345104ec388bfb4b53d33acdd1cded1c4a992a42ae8994fd0dcaac627`；本地原始 CRLF 回执 SHA256：`2c3dde59246b91afafcb8c0ac49778481ebfccb40fa95f4ce76c09d3f8920cc1`。归档仅归一化换行，JSON 数据不变；不包含原始正文、reasoning、原生 replay payload、headers、地址、凭据、账户或异常消息。主线程另用本次三个 API key 的运行时值检查，没有出现在回执中。

这些结果只覆盖当前四条配置路由、模型和有限题目。不证明所有模型支持摘要、每次必有摘要、其他协议的互相回放或服务端实际使用了哪部分隐藏状态。Chat/DeepSeek 本次仅离线验证；旧/未知 Anthropic 默认状态不推断；安全 redacted thinking 仍允许 null。

## 实际 NuGet 包

一次 Pack 的唯一开发版本：`0.1.0-dev.20261031095150.reasoning`。四包均来源于上述 commit，本地 feed：`artifacts/feed-0.1.0-dev.20261031095150.reasoning/`。没有覆盖公开版本。

| nupkg | SHA256 |
| --- | --- |
| Atelia.Diagnostics | 4f37f20db1fbf5bc5b459525b061f82aa5c348988ba390638790b05f3091145a |
| Atelia.Completion.Abstractions | 8926b00358b127f05adda9cf04e0aa90290f0d582675b764aaa244f4deeb68b7 |
| Atelia.Completion | acf2d35187ea4a69df90d599b9a52194e951f90f91aa0161f0f97329e1423905 |
| Atelia.Completion.Tools | 9ab89725d6675b51c65c2e2f4fed3398cc10b6980b51a19f69c501f958994cc4 |

`eng/Test-Package.ps1` 使用仓外新目录和隔离缓存，三个独立消费者均构建和执行成功，0 warnings/errors；校验六项消费缓存哈希与同一候选一致。新 public API smoke 实际覆盖 Responses/Gemini 的默认开启、显式关闭、接收服务端摘要、正文隔离和序列化；另保留既有 HTTP、终止、取消、usage 和工具执行覆盖。

消费者回执本地路径：`W:/TEMP/completion-reasoning-smoke-fdc139dcc0394db0b5a4776bcb92edce/package-smoke-results.json`。包是本地验收候选，不表示已从公开源安装或已发布。
