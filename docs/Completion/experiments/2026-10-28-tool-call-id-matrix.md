# Tool-call ID 实测：先保留原值，暂不引入兼容映射层

日期：2026-10-28。生产代码基线：`9dcc9f3d69dfe87b085eca8cae2d81bc1736610c`。
本次新增独立实验 runner，没有修改生产库、ActionMessage 或 ID 映射行为。

后续进展：同日已修改 Gemini adapter 并完成 4×4 全矩阵，见
[Gemini 历史兼容性报告](2026-10-28-gemini-history-compatibility.md)。下文保留修改前的实验记录。

## 结论与范围

当前已测组合没有出现必须转换 tool-call ID 的反例，支持暂缓来源绑定和兼容映射层。
补测中四个来源到三个可测目标的 **12 格全部通过**；包含 Anthropic `toolu_…` 与
Responses / Codex / Gemini `call_…` 的相互接收。没有改写 ID。

这不是完整的五端点全互通声明。Anthropic 和 OpenAI API 使用用户配置的同一转发站，
结论覆盖该入口，不代替厂商直连测试；Codex 与 Google 使用各自直连入口。
现有转发站的 Chat 路由返回 404，Gemini 目标在本地 converter 被原生 replay 载体要求挡住。
这两类缺口都不是 ID 格式拒绝。

实验是单工具、单来源样本的格式探针，不测试整个字符集、多工具碰撞、原生 reasoning 跨协议续接或缓存收益。
每格均使用统一合成历史，仅成对替换 call/result ID。
通过要求 Completed、没有新工具调用、准确读回仅存在于工具结果中的随机 token、来源 ID 有 HTTP 响应证据，
且 HTTP 请求的 call/result ID 与来源原值一致。不是仅凭 HTTP 200 判成功。

## 补测矩阵与实际样本

行是 ID 来源，列是接收端点。所有生成样本均核对了真实响应字段，不是 parser fallback。

| 来源 | Haiku 5.5 / Messages / 转发站 | GPT-6 Luna / Responses / 转发站 | GPT-6 Luna / Codex | Gemini 3.8 Flash / native |
| --- | --- | --- | --- | --- |
| Haiku 5.5 / Messages | 通过 | 通过 | 通过 | 本地拒绝，未测服务 |
| GPT-6 Luna / Responses | 通过 | 通过 | 通过 | 本地拒绝，未测服务 |
| GPT-6 Luna / Codex | 通过 | 通过 | 通过 | 本地拒绝，未测服务 |
| Gemini 3.8 Flash / native | 通过 | 通过 | 通过 | 本地拒绝，未测服务 |

| 来源 | 原始 tool-call ID |
| --- | --- |
| Haiku 5.5 / Messages | `toolu_01AsdH1WtffQYamn5GGErkN5` |
| GPT-6 Luna / Responses | `call_45ec05098a0f487f976414fa4fa6885b` |
| GPT-6 Luna / Codex | `call_fcc5d114b82842eaa10fa029c2879510` |
| Gemini 3.8 Flash / native | `call_79633` |

Responses 两轮生成观察到了相同的 ID；尚未调查原因，不能从本实验推断端点输出的 ID 跨请求全局唯一。

## 两轮账目与原始证据

| 运行 | 逻辑调用 | 模型 HTTP POST | 通过的回灌格 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 首轮 `20261028T102803062Z-370d390d` | 20 | 17 | 9 | 计划 5+25；Haiku 没生成 tool-call、Chat 生成 404，缺样本的两行共 10 格未调用；三个 Gemini 目标格本地拒绝 |
| 补测 `20261028T103210489Z-64c7ea37` | 20 | 16 | 12 | 移除无路由的 Chat；Haiku 生成阶段使用 required-named；四个 Gemini 目标格本地拒绝 |
| 合计 | 40 | 33 | 21 | 无 runner 自动重试；每次失败/未执行项均保留 |

首轮所有 Chat 请求（一次生成、三次回灌）均 HTTP 404；不能归因于 ID。
Haiku 首轮正常 Completed，但没有工具调用，因此没有捏造来源 ID；补测只改变其生成阶段的工具选择，
回灌仍使用默认选择。其余三个来源补测取得新的响应记录，不覆盖首轮。

框架之外另做了 **7 次 GET 元数据检查**：两家转发站默认 Python UA 返回 403，使用诚实的
`Atelia-ToolCallIdMatrix/1.0` UA 后 `/v1/models` 返回 404；Google 模型列表返回 200。
这些不是模型生成调用，不计入上面的 33 次 POST，也未当作模型不支持的证据。

- [首轮 JSONL](2026-10-28-tool-call-id-matrix/20261028T102803062Z-370d390d/attempts.jsonl) 与 [首轮自动矩阵](2026-10-28-tool-call-id-matrix/20261028T102803062Z-370d390d/report.md)。
- [补测 JSONL](2026-10-28-tool-call-id-matrix/20261028T103210489Z-64c7ea37/attempts.jsonl) 与 [补测自动矩阵](2026-10-28-tool-call-id-matrix/20261028T103210489Z-64c7ea37/report.md)。
- [首轮配置](2026-10-28-tool-call-id-matrix/initial.config.json)、[补测配置](2026-10-28-tool-call-id-matrix/followup.config.json)：只保存环境变量名，归档时调整输出目录相对路径。
- [最终实验源码 SHA-256](2026-10-28-tool-call-id-matrix/final-harness-sha256.json)：对应补测及交付的 runner，不追溯冒充首轮尚未增加 generationToolChoice 时的源码版本。

JSONL 包含实际 ID、出站配对字段、HTTP 状态、termination、usage 与失败事实；没有正文、headers、密钥或原生 reasoning。
主线程独立核对了通过格的配对与计数，并用当前凭据值检查归档无匹配泄漏。

## 继续使用

[实验使用说明](../../../experiments/ToolCallIdMatrix/USAGE.md) 和
[通用配置样例](../../../experiments/ToolCallIdMatrix/config.example.json) 已提供。
当前机器另有已预填的 `experiments/ToolCallIdMatrix/config.local.json`（被 git 忽略），包含补测的四个入口，
用户可以直接增加 endpoint/model 组合。新增端点时同步提高 `maxRequests`；完整矩阵逻辑调用数为 `N + N²`。

从仓库根运行：

```powershell
dotnet run --project experiments/ToolCallIdMatrix -c Release -- --validate experiments/ToolCallIdMatrix/config.local.json
dotnet run --project experiments/ToolCallIdMatrix -c Release -- --live experiments/ToolCallIdMatrix/config.local.json
```

`--validate` 不读凭据、不调用网络；`--live` 会产生实际请求。
API 地址使用本库约定的服务根目录，客户端自行追加 `v1/messages`、`v1/responses`、`v1/chat/completions`，
不要机械照抄其他 SDK 的 `/v1/` base URL。Codex 地址固定，不需要 API key。

按用户授权已从 WSL 补齐 Windows 用户级的 `CLAUDE_BASE_URL`、`CLAUDE_API_KEY`、`GPT_BASE_URL`、`GPT_API_KEY`，
保留已有 `GEMINI_API_KEY`。既有进程不会自动刷新环境；本轮运行从用户环境导入这些命名变量，
后续可重新启动终端/IDE 后运行。不在配置中填写密钥正文。

首轮模型选择为 `claude-haiku-5-5`、`gpt-6-luna`、`gemini-3.8-flash`，均使用 low effort。
Haiku 与 Gemini 的名称经[Anthropic 文档](https://platform.claude.com/docs/en/models/haiku-5-5/overview)、
[Google 文档](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash)及实际调用验证；
Google 模型列表还列有 `gemini-4-argon`，本次未调用它。

## 验证与后续裁决

Release 构建零警告、零错误；最终 24 项离线自检通过，包含四协议字段提取、Codex 诊断注入完整链路、
成功/不完整判定、随机结果隔离、凭据与原生 payload 不出现在证据、生成强制选择不泄漏至回灌。
主线程实际重跑了离线自检，并执行以上两轮 live。未修改生产库，不以此宣称完整库回归或包发布验收。

下一步是追加可用 Chat 兼容端点、厂商直连或不同转发站再测。
若出现候选 ID 失败而同目标的已知合法 ID 成功，再做单变量对照并设计最小映射规则。
在此之前，继续原样保留已捕获的 ID；来源承载改造与通用映射器保持暂缓。
