# Gemini 历史兼容性：实现、在线证据与边界

日期：2026-10-28。实验分支：`experiment/gemini-history-compatibility`。
最终实现提交：`e2cfab7`。用户授权在实验取得进展后合并本地主线；没有推送或发布。

## 结果

之前阻止异源工具历史进入 Gemini 的主要原因是本地 adapter，并非已证实的服务器限制。
修改后，四端点 **4×4 有向 ID 矩阵全部通过**，不改写任何 call-id。
Claude Haiku 5.5、GPT-6 Luna Responses、GPT-6 Luna Codex 的实际生成消息，都能交给
Gemini 3.8 Flash 在工具循环中途或完整一轮之后继续；Gemini 自己的原生签名也原样回放。
当前证据继续支持暂缓通用 ID 映射层和 ActionMessage 来源绑定重构。

## 实现原则

- 有匹配 provider/API 的 GeminiReplayBlock：保留原生 parts、签名及 call-id，不按模型名阻止切换。
- 没有匹配原生块：投影可见正文及 JSON 工具调用。每个 action 的首个 functionCall 添加
  `skip_thought_signature_validator`，后续并行调用不添加。异源 opaque payload 不发送。
  标记按 action 固定，追加新 user 回合不会改写旧投影前缀。
- 调用与结果的缺失、重复、空 ID、名称错配及消息邻接关系由服务器判断。
  完整且唯一对应的结果仍按调用顺序排列；其他情况保持输入顺序和值。
- 原生 replay 的可选 ID 可缺失或为 null，空白字符串原样保留，不额外规定 ID 字符集。
  这不改变生成时已经捕获或补齐 ID 的行为。
- 工具定义使用 `parametersJsonSchema`，保留 JSON Schema 字段，不再转换成旧 `parameters` 子集。
  forced tool choice 也不再依据本地 catalog 的支持布尔值提前拒绝。
- 仍验证真实的表示边界：参数必须是有效 JSON object；native payload 必须能解码，且不能与
  可见消息矛盾。只有异源 opaque 块而没有可见内容时无法生成有意义的 model 内容。
  非 JSON 工具、协议不能兑现的禁止并行承诺、无法映射的 reasoning 配置仍明确失败。
  不增加自动重试；服务器拒绝沿用 CompletionFailureException.Failure。

官方依据：[Generate Content thought-signatures FAQ](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures#faqs)
明确提供跨模型工具轨迹导入标记；同页区分当前 turn 的签名验证与此前回合。
JSON Schema 字段见 [FunctionDeclaration API](https://ai.google.dev/api/generate-content#FunctionDeclaration)。
这里使用 Generate Content API，未混用 Interactions 协议。

## 本次在线实验

| 实验 | 模型 POST | 结果 | 原始白名单证据 |
| --- | ---: | --- | --- |
| 实际生成消息：四来源的当前回合/下一回合 | 16 | 4 次生成、4 次来源端完成、8 次 Gemini 续接均 Completed；12 次回放核验通过 | [history.jsonl](2026-10-28-gemini-history-compatibility/20261028T141219584Z-history-1976b052/history.jsonl) |
| 统一合成历史的 4×4 ID 矩阵 | 20 | 4 次生成 + 16 格 PASS | [矩阵与原始 ID](2026-10-28-gemini-history-compatibility/20261028T145852874Z-d56fbd69/report.md)、[JSONL](2026-10-28-gemini-history-compatibility/20261028T145852874Z-d56fbd69/attempts.jsonl) |
| 最终提交的 Gemini 原生复验 | 4 | 1 次生成 + 3 次回放通过；真实签名逐字一致，无导入标记 | [history.jsonl](2026-10-28-gemini-history-compatibility/20261028T150332415Z-history-768ff9f3/history.jsonl) |

合计 **40 次模型 POST，全部 Completed**，没有其他 HTTP 请求、没有 runner 重试。
所有矩阵样本的 ID 都在源 HTTP 响应中核验；目标请求的 call/result ID 均保持来源原值。
矩阵 PASS 还要求无新增工具调用、准确返回仅存在于工具结果中的随机 token。

实际消息实验保留来源生成的 ActionMessage。当前回合的 token 只在工具结果中；下一回合的
token 已经同时存在于来源模型的上一轮正文，因此 next-turn 成功证明整体历史接受与续接，
不能独立证明模型依靠工具配对读取答案。真实签名仅在内存比较，回执只记录计数及布尔值。

前两轮运行于实验分支的未提交改动：日志中的 HEAD `a2093ed` 只是当时基线，并不单独标识运行实现。
之后复核补齐 native 空/缺失 ID 的解码边界并加入 history 日志 commit 字段，提交为 `e2cfab7`；
最终四次复验明确针对该提交。最终源文件哈希见 [manifest](2026-10-28-gemini-history-compatibility/final-source-sha256.json)。
前两轮并未覆盖新增 native 空 ID 边界，该项为离线验证。

## 验证及适用范围

- Windows、根 global.json SDK、Release 全离线回归：1,198 passed，0 failed，3 skipped。
  三个跳过项是 Unix 文件权限/所有者相关测试。
- 随后补齐 native ID 边界后，Gemini 相关测试再次执行：165 passed，0 failed。
- 实验 runner Release 构建 0 warning/error，离线 self-test 24 项通过。
- 独立审查发现的 native ID 预拒绝已修复并复核。未增加 public API、包依赖或 IVT。
- 没有执行 Linux 回归、打包或公开 NuGet 发布；未改包元数据或包 README。

Anthropic Messages 和 OpenAI Responses 使用用户配置的同一转发站 `aicoding.0011.ai`，
不能替代厂商官方直连结论；Codex 使用 chatgpt.com，Gemini 使用 Google 直连。
本轮没有重试原先返回 404 的 Chat 路由。
线上覆盖单工具、完整合法历史和当前 Gemini 3.8 Flash；并行/多步及不完整配对的放行有离线测试，
不代表服务器一定接受它们。Gemini 2.5/4、Vertex、任意签名跨服务有效性、ID 长度/字符全集及缓存命中率均未验证。

## 复跑

从仓库根运行（凭据只由环境变量引用）：

```powershell
dotnet run --project experiments/ToolCallIdMatrix -c Release -- --live docs/Completion/experiments/2026-10-28-gemini-history-compatibility/four-endpoints.config.json
dotnet run --project experiments/ToolCallIdMatrix -c Release -- --gemini-history docs/Completion/experiments/2026-10-28-gemini-history-compatibility/four-endpoints.config.json
```

用户可继续编辑忽略提交的 `experiments/ToolCallIdMatrix/config.local.json` 扩充端点与模型；
history 模式要求其中恰有一个 Gemini 目标。配置、预算、结果规则详见 [USAGE](../../../experiments/ToolCallIdMatrix/USAGE.md)。
