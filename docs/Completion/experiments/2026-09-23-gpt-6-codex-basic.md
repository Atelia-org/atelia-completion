# GPT-6 Sol / Luna Codex 基础适配实测（2026-09-23）

## 验收范围

目标是验证 `OpenAICodexResponsesClient` 经 ChatGPT Codex 订阅后端，对
`gpt-6-sol` 和 `gpt-6-luna` 的普通文本流及同模型下一轮对话可用。
本次不验证 GPT-6 新增的中途调整 reasoning effort、工具开关、工具调用、
跨模型 reasoning 回放或公开 OpenAI API 的账号权限。

测试入口为 `OpenAICodexGpt6BasicLiveTests`。显式 opt-in 后，每个模型依次发出
两次可能计费的 generation：首次要求返回固定短标记；第二次带上首次
`Completed` 的 `ActionMessage`，询问前一轮标记。使用默认 reasoning effort、
无工具，宿主为每次调用提供两分钟 `CancellationToken` 期限。
每次要求 HTTP 200、`Completed`、非空正文、至少一个正文增量，且正文包含标记。
标记判断是小型语义检查，不承担通用模型质量评测。

## 复现

在仓库根目录执行；真实凭据只在运行时读取，测试不会登录、刷新或写回：

```bash
ATELIA_RUN_CODEX_GPT6_BASIC_LIVE=1 \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE="$HOME/.codex/auth.json" \
dotnet test tests/Completion.Tests/Completion.Tests.csproj \
  -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~OpenAICodexGpt6BasicLiveTests' \
  --logger 'console;verbosity=detailed'
```

没有 opt-in 时测试体提前返回；这不算真实调用通过。默认 CI 显式将开关设为 `0`。
命令要使用操作员选定的绝对 `auth.json` 路径；`CODEX_HOME` 指向别处时，
不要假定 `$HOME/.codex/auth.json` 就是当前 Codex 使用的账号。

若要诊断真实 HTTP 文本交换，可额外设置
`ATELIA_CODEX_GPT6_BASIC_RAW_LOG_DIR=<existing-absolute-private-directory>`。
仅 Linux 支持该选项。每个模型在该目录得到一个全新 JSONL 文件，记录完整请求正文和
实际读到的响应 SSE，不记录请求 header。此文件包含 prompt 和可能的原生 reasoning
payload，不应提交；诊断后删除。回放只验证固定请求和响应下的适配行为，
不能代替真实服务验收，也不能证明服务端最终使用的模型身份。

## 本次结果

来源工作树起点 commit：`649f282`；本次探针在该基线上新增并实测。
平台：Linux，.NET SDK `10.0.201`，Release。使用本机 file-backed Codex 凭据。
未启用 raw 录制；下表来自本次真实 LiveE2E 测试输出，不是历史 fixture。

| 模型 | 阶段 | HTTP | 终止 | 正文长度 | 正文增量数 | 包含标记 | 耗时 |
| --- | --- | ---: | --- | ---: | ---: | --- | ---: |
| `gpt-6-luna` | 首轮 | 200 | Completed | 7 | 4 | 是 | 1973 ms |
| `gpt-6-luna` | 续轮 | 200 | Completed | 7 | 4 | 是 | 1687 ms |
| `gpt-6-sol` | 首轮 | 200 | Completed | 7 | 4 | 是 | 2603 ms |
| `gpt-6-sol` | 续轮 | 200 | Completed | 7 | 4 | 是 | 1793 ms |

首次运行两个测试案例均通过，共四次真实调用。随后以私有临时目录启用 raw
录制，再运行相同的两个案例，共四次真实调用，均通过。每个模型的 JSONL
均为 `0600`、两条 HTTP 200 exchange；请求 `model` 与服务端
`response.completed.response.model` 均分别为 `gpt-6-luna` 或 `gpt-6-sol`，
四条记录均含 `response.completed`。只提取了这些元数据，临时原始文件已删除。

合计八次真实调用。结果证明上述两个模型在本次 Codex 路由、账号和时间点下
能完成基础文本及同模型续轮，且服务端 terminal 报告的模型身份与请求一致；
不代表未来后端兼容性、其他账号权限或尚未测试的新功能。
