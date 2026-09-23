# Claude Opus 5.5 Anthropic Messages 适配实测（2026-09-24）

## 验收范围

目标是验证 `AnthropicClient` 通过操作员指定的 Messages 中转站，对
`claude-opus-5-5` 的基础文本流与同模型下一轮对话可用。官方
[模型页](https://platform.claude.com/docs/en/models/opus-5-5/overview)
确认该 Claude API 模型 ID 和标准 Messages 最大输出 128K tokens。
本次使用客户端默认 reasoning effort 与 prompt caching 设置、无工具；
不验证 Opus 5.5 的新特性、工具调用、跨模型 thinking 回放或直连 Anthropic
官方 API 的账号权限。

`AnthropicOpus55BasicLiveTests` 是显式 opt-in 的真实调用测试。一次运行依次
发出两次可能计费的 generation：首次要求回复固定短标记；第二次带上首次
`Completed` 的 `ActionMessage`，询问前一轮标记。每次调用使用宿主提供的
三分钟 `CancellationToken` 期限，并要求 Messages HTTP 200、`Completed`、
非空正文、至少一个正文增量，以及正文包含标记。

`AnthropicClient` 发送 Messages 前会查询 `GET v1/models/claude-opus-5-5`。
本次中转站对该查询返回 HTTP 404，因此加入该精确模型 ID 的 128K
`max_tokens` 回退，并用离线用例覆盖 404 和 405。回退仅适用于既有允许的
404/405/501 状态；认证、限流、服务错误与畸形成功响应仍失败。

## 复现

测试运行时从环境读取 `CLAUDE_BASE_URL` 和 `CLAUDE_API_KEY`，不打印两者的值。
`CLAUDE_BASE_URL` 应指向 HTTPS 中转站 API 根前缀；客户端追加相对路径
`v1/models/{model}` 和 `v1/messages`，不要预先在该值末尾加入 `/v1`。
在仓库根目录执行：

```bash
ATELIA_RUN_ANTHROPIC_OPUS55_BASIC_LIVE=1 \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~AnthropicOpus55BasicLiveTests' \
  --logger 'console;verbosity=detailed'
```

未设置 opt-in 时测试体提前返回，不构成在线通过。CI 将开关固定为 `0`。
输出只记录模型、阶段、HTTP/terminal 状态、正文长度、增量数与标记布尔值，
不记录正文、凭据或 Base URL。

需要核对 HTTP/SSE 交换时，可设置
`ATELIA_ANTHROPIC_OPUS55_RAW_LOG_DIR=<existing-absolute-private-directory>`。
该选项仅在 Linux 可用，生成新的 JSONL 文件，包含完整请求正文和实际读取到的
响应内容，但不包含请求 header。原始文件可能包含 prompt、thinking 与签名等
敏感数据，应放入私有临时目录，提取必要元数据后删除；回放不替代在线验收。

## 本次结果

来源基线 commit：`3067be0`。平台：Linux，.NET SDK `10.0.201`，Release。
环境中的 `CLAUDE_BASE_URL` 是 HTTPS 根地址，`CLAUDE_API_KEY` 已配置；
其值未记录。首次真实调用：

| 阶段 | Models HTTP | Messages HTTP | 终止 | 正文长度 | 正文增量数 | 包含标记 | 耗时 |
| --- | ---: | ---: | --- | ---: | ---: | --- | ---: |
| 首轮 | 404 | 200 | Completed | 8 | 1 | 是 | 3670 ms |
| 续轮 | 已缓存 | 200 | Completed | 8 | 1 | 是 | 2131 ms |

再次在权限为 `0700` 的私有临时目录启用 raw 录制，两轮仍通过，Messages
均为 HTTP 200 与 `Completed`。临时 JSONL 文件权限为 `0600`，包含一次
Models 404 及两次 Messages 200 exchange。两次 POST 的请求模型均为
`claude-opus-5-5`、`max_tokens=128000`；第二次带有一次 assistant 历史轮次。
两次响应的 `message_start.message.model` 均声明 `claude-opus-5-5`，并均收到
`message_delta` 与 `message_stop`。原始 JSONL 已删除。

合计四次真实生成请求。离线受影响测试先行通过 41 项；完整离线测试为
858 项通过，1 项 Windows 专属测试在 Linux 跳过。此结果证明当前中转站、
凭据与时间点下的基础文本及同模型续轮可用。中转站返回的模型字段无法
单独证明其内部实际使用的上游模型。

后续将已知模型的输出上限改为直取，见
[已知模型输出上限直取实测](2026-09-24-anthropic-known-model-maximum.md)。
