# Tool-call ID 有向兼容性实验

独立 .NET 10 console，直接引用本仓 Completion。只测真实来源 ID 放进统一合成工具历史后能否被目标接受并正确读回结果；不改写 ID，不承诺任意原生 reasoning 跨模型续接。可另用 history 模式验证实际生成消息的续接。

从仓库根运行：

```powershell
dotnet run --project experiments/ToolCallIdMatrix -- --self-test
dotnet run --project experiments/ToolCallIdMatrix -- --validate experiments/ToolCallIdMatrix/config.example.json
# 复制为 config.local.json，填 modelId、地址和环境变量名后显式在线执行：
dotnet run --project experiments/ToolCallIdMatrix -- --live experiments/ToolCallIdMatrix/config.local.json
```

只有配置路径时等同 `--validate`，不读凭据、不访问网络。离线验证拒绝未知/重复属性、非法端点、明文 apiKey 字段；不证明环境变量存在、模型可用或网络可达。样例 modelId 故意是占位符，请填写实际服务允许的模型。一次 `N + N²` 有向计划，每个来源生成一个样本，包含对角线及两个方向。没有随机重试；不支持并行调用。

## 配置

- `version` 固定 `1`。`endpoints` 为参与本次运行的完整端点列表，删除暂不参与的项即可。
- `id` 为唯一 ASCII 字母数字、`-`、`_` 标识；`kind` 支持 `openai-chat`、`openai-responses`、`anthropic`、`gemini`、`openai-codex-responses`。
- API key 端点配置 `baseAddress` 或 `baseAddressEnv` 二选一，以及可选 `apiKeyEnv`。禁止把秘密写进配置。运行时复用现有 `CompletionConnectionConfigLoader.NormalizeAndValidateCatalog` 解析环境变量。
- `baseAddress` 是服务根地址；本仓客户端会追加协议路径（例如 OpenAI 追加 `v1/chat/completions`、`v1/responses`），通常应填 `https://api.openai.com/`，不要重复包含 `v1/`。自建代理有额外前缀时，按其实际路由填写。
- Chat 可指定 `completionSurfaceId`，默认 `openai-chat/strict`；还支持现有 `sglang-compatible`、`qwen-sglang`、`deepseek-v4` surface（完整前缀均为 `openai-chat/`）。其他普通客户端 surface 默认为 kind。
- `reasoningEffort` 使用库现有值：`provider-default`、`disabled`、`low`、`medium`、`high`、`xhigh`、`max`。模型是否支持由当前库/服务判定。
- `generationToolChoice` 默认 `provider-default`，可为 `required-any` 或 `required-named`。仅生成阶段使用，所有回灌仍为 provider-default；只对确知支持强制调用的模型启用。未产生单个预期工具的样本明确未执行后续行。该选项写入 run 元数据；改配置复验产生新 run，不覆盖旧失败。
- Codex 使用已有 `OpenAICodexResponsesClient`，禁止设置 baseAddress/API key/surface。可选 `codexAuthFile` 必须为绝对路径；省略使用现有 CLI auth 默认定位。首次读取快照并 pin 其 AccountFingerprint；可选 `codexAccountFingerprintEnv` 要求显式预期指纹。只读凭据，不 login/refresh/write-back，不输出账户或指纹。
- `maxRequests` 为 completion 逻辑调用上限（含本地投影失败），不是 token/费用上限。能力查询及 Codex 已有 401 credential-reload 请求分别列入实际 HTTP 计数；一次逻辑调用可能多个 HTTP 请求。`requestTimeoutSeconds` 在宿主转成 CancellationToken；超时后远端结果可能未知。
- `outputDirectory` 相对配置文件所在目录；每次创建唯一 run 子目录，不覆盖旧证据。输出 `attempts.jsonl` 与 `report.md`。Ctrl+C 取消当前请求并将剩余项标未执行。

## 证据和判定

生成记录同时保留完整 parser ID 和 HTTP 响应的白名单 ID 字段；只有两者一致才称 wire verified。Responses 使用 `call_id`，绝不混用 output item 的 `id`。没找到匹配证据只表示未证实，不断言一定为 parser fallback。HTTP 请求白名单记录实际 call/result ID；不保存请求或响应正文、headers、URL、秘密、账户或原生 reasoning。

每个目标收到同一个工具定义、参数、结果和消息顺序，唯一来源差异是成对的 ID。结果包含运行时随机 token，提示和 user 消息没有答案。矩阵 PASS 要求：Completed、无新增工具调用、精确返回该 token、源 ID 有 wire 证据、发出的 call/result ID 都保持原值。HTTP 200、单独 Completed 或看到某个 ID 字符串均不够。Failed、Incomplete、HTTP 错误、transport/取消后未知、本地拒绝、预算/来源缺失未执行分别记录；不靠自然语言错误文本推断 ID 不兼容。

Gemini adapter 会将无原生 replay 的合成工具历史投影为 functionCall，使用官方的 `skip_thought_signature_validator` 导入标记，原始 ID 不变。服务未返回 wire ID 时，本仓补齐的 ID 不算厂商格式证据。

Codex 的 public API 目前没有 capture 注入。因此实验代码仅反射两个既有 internal diagnostic 签名：`CompletionHttpClientBuilder.BuildHandler()` 和 Codex 的 `(credentialProvider, options, HttpMessageHandler)` 构造器。不访问字段、不绕过认证/账户绑定/固定路由；签名变化明确失败。这个源码绑定属于实验，不是推荐的公共客户端用法；离线自检通过 fake handler 覆盖该路径。其他客户端都使用 public ctor 与 public capture builder。

证据只覆盖本次 endpoint/protocol/model/时间和有限 ID 样本，不证明整个字符集、所有历史长度或缓存命中。遇到 HTTP 拒绝，应补同目标已接受 ID 的单变量对照再讨论映射；认证、限流、模型退役、签名要求不能直接解释为 ID 格式不兼容。多调用/逆序结果、同源重命名、字符长度边界及完整原生基线尚未纳入这个最小 runner。

## 实际消息的 Gemini 续接实验

```powershell
dotnet run --project experiments/ToolCallIdMatrix -c Release -- --gemini-history experiments/ToolCallIdMatrix/config.local.json
```

显式在线模式，配置必须恰有一个 `kind=gemini` 目标。每来源最多 4 次调用：生成工具调用、交给 Gemini 在当前 tool-loop 读结果、原来源完成这一轮、加入新 user 消息后交给 Gemini 续接。沿用实际生成的 ActionMessage，包括 native blocks；目标 adapter 决定原生回放或可见投影。4N 预算，顺序调用，不重试。来源未成功生成单个预期工具或未正确完成时，其依赖步骤跳过，实际调用数写入 summary。

`history.jsonl` 仅记录 ID、HTTP/termination、精确答案与配对核验、原生签名计数及逐字相等布尔值、导入标记计数。真实签名仅在内存比较，不写回执。新回合的答案也存在于上一轮模型正文，所以 next-turn 证明会话续接，不能独立证明模型从工具结果取值。原始 ID 格式的隔离验证仍以矩阵模式为准。
