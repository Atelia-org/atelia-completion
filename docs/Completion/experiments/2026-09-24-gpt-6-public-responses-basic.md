# GPT-6 Sol / Luna 普通 Responses 适配实测（2026-09-24）

## 验收范围

目标是验证 `OpenAIResponsesClient` 使用普通 API Bearer 凭据，经操作员指定的
中转站，对 `gpt-6-sol` 和 `gpt-6-luna` 的文本流及同模型下一轮对话可用。
这与 [Codex 订阅路由实测](2026-09-23-gpt-6-codex-basic.md) 是两个独立验收。
本次不验证 GPT-6 新增的中途调整 reasoning effort、工具开关、工具调用、
跨模型 reasoning 回放或直连 OpenAI 官方 API 的账号权限。

测试入口为 `OpenAIResponsesGpt6BasicLiveTests`。显式 opt-in 后，每个模型依次
发出两次可能计费的 generation：首次要求返回固定短标记；第二次带上首次
`Completed` 的 `ActionMessage`，询问前一轮标记。使用默认 reasoning effort、
无工具，宿主为每次调用提供两分钟 `CancellationToken` 期限。
每次要求 HTTP 200、`Completed`、非空正文、至少一个正文增量，且正文包含标记。
标记判断是小型语义检查，不承担通用模型质量评测。

## 复现

`GPT_BASE_URL` 和 `GPT_API_KEY` 只在运行时从环境读取。`GPT_BASE_URL` 应指向
中转站的 API 根前缀；client 会追加相对路径 `v1/responses`。不要在该值末尾
预先加入 `/v1`。在仓库根目录执行：

```bash
ATELIA_RUN_OPENAI_RESPONSES_GPT6_BASIC_LIVE=1 \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~OpenAIResponsesGpt6BasicLiveTests' \
  --logger 'console;verbosity=detailed'
```

没有 opt-in 时测试体提前返回；这不算真实调用通过。默认 CI 显式将开关设为 `0`，
并按常规关闭其他 live 开关。测试输出仅写模型、阶段、HTTP/terminal 状态、
正文长度、增量数和标记布尔值，不写 API Key、Base URL 或响应正文。

若要诊断真实 HTTP 文本交换，可额外设置
`ATELIA_OPENAI_RESPONSES_GPT6_RAW_LOG_DIR=<existing-absolute-private-directory>`。
仅 Linux 支持该选项。每个模型在该目录得到一个全新 JSONL 文件，记录完整请求正文和
实际读到的响应 SSE，不记录请求 header。此文件可能包含敏感 prompt 和原生
reasoning payload，不应提交；诊断后删除。回放只能验证固定请求和响应下的
客户端行为，不能代替真实服务验收。

## 本次结果

来源基线 commit：`a2693fc`；本次探针在该基线上新增并实测。
平台：Linux，.NET SDK `10.0.201`，Release。`GPT_BASE_URL` 为已配置的 HTTPS
中转站根地址；`GPT_API_KEY` 已配置。未记录两者的值。下表来自首次真实
LiveE2E 测试输出，不是历史 fixture。

| 模型 | 阶段 | HTTP | 终止 | 正文长度 | 正文增量数 | 包含标记 | 耗时 |
| --- | --- | ---: | --- | ---: | ---: | --- | ---: |
| `gpt-6-sol` | 首轮 | 200 | Completed | 7 | 4 | 是 | 3172 ms |
| `gpt-6-sol` | 续轮 | 200 | Completed | 7 | 4 | 是 | 2226 ms |
| `gpt-6-luna` | 首轮 | 200 | Completed | 7 | 4 | 是 | 3876 ms |
| `gpt-6-luna` | 续轮 | 200 | Completed | 7 | 4 | 是 | 3189 ms |

首次运行两个测试案例均通过，共四次真实调用。随后以私有临时目录启用 raw
录制，再运行相同的两个案例，共四次真实调用，均通过。每个模型的 JSONL
均为 `0600`、两条 HTTP 200 exchange；请求 `model` 与中转站返回的
`response.completed.response.model` 均分别为 `gpt-6-sol` 或 `gpt-6-luna`，
四条记录均含 `response.completed`。只提取了这些元数据，临时原始文件已删除。

最后收紧测试对 `GPT_BASE_URL` 的校验：要求 HTTPS，且不带 user info、query
或 fragment。重新构建并再次执行两个 LiveE2E 案例，各模型两轮仍全部通过，
四次请求均为 HTTP 200、`Completed`、正文长度 7、四次正文增量，且包含标记。
完整离线测试为 856 通过、1 项 Windows 专属测试在 Linux 跳过。

合计十二次真实调用。结果证明两个模型在本次中转站、凭据和时间点下能完成
基础文本及同模型续轮，且返回的 terminal 报告模型身份与请求一致；
中转站报告的模型字段不能单独证明其内部实际使用的上游模型。
