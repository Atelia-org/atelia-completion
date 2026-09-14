# Atelia Completion

可独立复用的 .NET LLM 客户端、请求合同和工具执行组件，以及不依赖 LLM 的 Diagnostics 调试库。四个项目独立成包，同仓不要求一起引用。

| 包 | TFM | 用途与直接库依赖 |
| --- | --- | --- |
| `Atelia.Diagnostics` | `netstandard2.0` | 调试日志；不依赖另三个包 |
| `Atelia.Completion.Abstractions` | `net10.0` | 请求、消息、完成状态、usage、工具合同；不依赖 Diagnostics |
| `Atelia.Completion` | `net10.0` | OpenAI Chat/Responses、Anthropic、Gemini、DeepSeek 与 Codex 客户端；依赖 Abstractions、Diagnostics |
| `Atelia.Completion.Tools` | `net10.0` | DTO/schema、方法包装、权限和执行；依赖 Abstractions、Diagnostics，不依赖 Completion 实现 |

源码来自 Atelia，保留现有程序集名和 namespace。提取身份见 [来源记录](docs/extraction-origin.md)。本库使用 MIT 许可证；当前为持续演进的预览 API。

## 安装与离线示例

首次公开目标版本为 `0.1.0-preview.1`，**当前尚未发布**。此时请使用本地候选 feed；不要把目标版本当成 nuget.org 上已经可取得的版本。公开发布前需将本段更新为实际发布说明，并检查实际 nupkg 内的 README 和固定版本链接。

调用模型只需直接引用 `Atelia.Completion`；它会传递引入所需合同和 Diagnostics。只需要调试日志或定义模型无关接口时，可分别只引用 Diagnostics 或 Abstractions。

[快速上手](docs/Completion/quick-start.md) 提供完整的 `PackageReference` 项目与文本示例：真实 `OpenAIChatClient` 处理内存中的 HTTP/SSE 响应，不需要模型服务、凭据或 localhost 进程。包首次还原仍可能需要联网。它证明包与 public API 可以工作，真实在线调用另行验证。

## 源码开发与候选包

在本仓根目录执行，使用 `global.json` 指定的 SDK：

```powershell
dotnet build Atelia.Completion.slnx -c Release
dotnet test tests/Completion.Tests/Completion.Tests.csproj -c Release --filter "Category!=LiveE2E&Category!=LocalE2E"

$version = '0.1.0-dev.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$feed = Join-Path $PWD "artifacts/feed-$version"
$work = Join-Path ([IO.Path]::GetTempPath()) ('completion-smoke-' + [Guid]::NewGuid().ToString('N'))
pwsh -File eng/Pack.ps1 -Version $version -OutputDirectory $feed
pwsh -File eng/Test-Package.ps1 -Version $version -FeedDirectory $feed -WorkDirectory $work
```

离线测试前按 [.github/workflows/ci.yml](.github/workflows/ci.yml) 关闭显式 live opt-in 开关和 `OPENROUTER_API_KEY`；默认测试不应读取真实凭据。Pack 要求已提交的干净源码、正确 origin 和全新或空输出目录。不同内容用不同版本，多个验证者共享一次 Pack 的候选 feed，不重建同版不同内容。`Test-Package.ps1` 使用独立 public API 消费者；其结果与源码测试分别记录。普通消费仓在公开交付后直接 restore，不要求先 clone 本仓。

Atelia 的源码联调计划在迁移 P2 实现，目前尚不可据此假定可用。目标配置是显式设置 `UseCompletionSources=true` 与本仓绝对路径 `CompletionSourceRoot`，切换模式后重新 restore。源码模式只用于 build/test；需要下游 pack 时先制作唯一版本开发包。生效状态以 Atelia 的依赖指南为准，不从兄弟目录自动选择依赖身份。

## 使用合同与文档

- [快速上手与宿主责任](docs/Completion/quick-start.md)：请求、结果、生命周期、取消、usage 与 reasoning。
- [传输与 provider terminal 合同](src/Completion/README.md)：Completed/Incomplete/Failed、流中断、协议证据与 Codex 文件凭据。
- [Tools 用法](src/Completion.Tools/README.md)：`MethodToolWrapper`、`ArtifactToolWrapper<T>`、`ToolSession`；副作用事务仍由宿主负责。
- [Diagnostics 用法](src/Diagnostics/README.md)：类别与日志级别、Release 调用裁剪、源码 Debug 联调。
- [HTTP 管线](docs/Completion/http-transport-pipeline.md)与 [provider 演进](docs/Completion/openai-compatible-evolution.md)：修改实现时阅读。

Source Link 可定位与包匹配的源码，但不会自动把文档加载进 Coding Agent 上下文。消费仓应记录包版本与对应 commit/tag；公开 `0.1.0-preview.1` 的文档入口将是 [固定版本快速上手](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/docs/Completion/quick-start.md)，发布前该 tag 链接可能尚不可访问。

`docs/Completion/experiments/` 是带日期的历史证据，不是当前版本已通过在线验收的声明，也不是构建前提。
