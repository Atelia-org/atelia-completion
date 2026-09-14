# Atelia Completion 快速上手

本文是 public API 使用入口；provider 精确终止规则见 [传输合同](../../src/Completion/README.md)，工具执行见 [Tools README](../../src/Completion.Tools/README.md)。实现与历史背景再读 [memory-notebook](memory-notebook.md)，不用先搭建本地模型服务。

## 1. 运行一个无模型服务的文本示例

在仓外独立目录建立下面两个文件。示例运行时完全在进程内使用固定 HTTP/SSE 响应，实际执行 `OpenAIChatClient` 的请求投影、解析和聚合；不访问 `example.invalid`。这不是在线模型验收。

`CompletionExample.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Atelia.Completion" Version="$(CompletionPackageVersion)" />
  </ItemGroup>
</Project>
```

`Program.cs`：

```csharp
using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("https://example.invalid/"), new OfflineHandler());
var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient);
var request = new CompletionRequest(
    "offline-example",
    new CompletionPromptPrefix(
        "Answer briefly.",
        CompletionOutputContract.ProviderDefault([]),
        [new ObservationMessage("Say hello.")]),
    tailMessages: []);

// 期限是这个示例的宿主政策，不是库的默认 timeout。
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
CompletionResult result = await client.StreamCompletionAsync(
    request, observer: null, cancellationToken: deadline.Token);
if (result.Termination.Kind != CompletionTerminationKind.Completed) {
    throw new InvalidOperationException($"Completion ended: {result.Termination.Kind}");
}
Console.WriteLine(result.Message.GetFlattenedText()); // hello

sealed class OfflineHandler : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream")
        });
    }
}
```

先在新仓按 [README](../../README.md#源码开发与候选包) 生成一个唯一版本候选包。此时首次公开目标 `0.1.0-preview.1` 尚未发布，示例必须使用实际候选版本。创建显式 `NuGet.Config`，把四个 Atelia 包准确映射到该候选 feed；其他依赖可从 nuget.org 还原：

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="completion" value="ABSOLUTE_PATH_TO_CANDIDATE_FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="completion">
      <package pattern="Atelia.Diagnostics" />
      <package pattern="Atelia.Completion.Abstractions" />
      <package pattern="Atelia.Completion" />
      <package pattern="Atelia.Completion.Tools" />
    </packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

在示例目录执行，将版本替换成刚才 Pack 的实际值；独立缓存避免以前还原过的同版包掩盖来源：

```powershell
$version = 'ACTUAL_CANDIDATE_VERSION'
$env:NUGET_PACKAGES = Join-Path $PWD '.packages'
dotnet restore CompletionExample.csproj --configfile NuGet.Config -p:CompletionPackageVersion=$version
dotnet run --project CompletionExample.csproj -c Release --no-restore -p:CompletionPackageVersion=$version
```

也可直接运行新仓 `eng/Test-Package.ps1 -Version <实际版本> -FeedDirectory <绝对feed路径> -WorkDirectory <新临时目录>`，由脚本完成隔离的包消费验证。公开版本完整可取得后，普通项目只需明确版本 `PackageReference` 与 nuget.org；无需本地 feed 或源码检出。

## 2. 换成真实服务

保留请求与完成状态检查，移除 `OfflineHandler`，将 `CreateLiveClient` 的 base address 替换为实际服务地址，再向匹配协议的 client 提供运行时凭据与真实模型名。endpoint 前缀由服务配置决定，不默认假设 localhost 或将 API Key 写入源码。不同协议使用对应 client：

| 协议 | public client |
| --- | --- |
| OpenAI Chat | `OpenAIChatClient`；需要方言时显式选择 `OpenAIChatDialects` |
| OpenAI Responses | `OpenAIResponsesClient` |
| Anthropic Messages | `AnthropicClient` |
| Gemini | `GeminiClient` |
| DeepSeek V4 Chat | `DeepSeekV4ChatClient` |
| Codex 文件凭据 | `OpenAICodexResponsesClient`，见 [凭据与协议边界](../../src/Completion/README.md) |

注入普通 `HttpClient` 的 provider client 不负责释放该 HttpClient，宿主可长期复用，最后释放。`CompletionHttpTransportFactory` 返回的 HttpClient 拥有其 handler 管线。`OpenAICodexResponsesClient` 自己拥有 HttpClient 和内部资源，需要 `using`/`Dispose`；不要把两种所有权混用。

Codex 文件凭据支持 Windows/Linux。客户端借用 access-token snapshot，不 login、refresh 或写回；仅有 OS keyring 而没有 auth.json 的场景不在支持范围。raw exchange 文件记录 sink 仍有限定平台，普通客户端调用不要求启用它。

## 3. 宿主必须处理的结果与取消

- `Completed`：协议确认完成后，才将 `Message.GetFlattenedText()` 交给成功业务处理；正文合规与业务格式仍需宿主验证。
- `Incomplete` / `Failed`：属于明确非成功结果，不能把部分正文当作成功。具体 terminal 规则以 [传输合同](../../src/Completion/README.md) 为准。
- `CompletionStreamInterruptedException`：terminal 前流中断，远端结果不确定；不能透明重试。HTTP/协议错误按实际异常传播。
- transport 没有 operation/idle timeout。调用者通过 CancellationToken 控制期限；接入已有 RequestTimeout 的宿主时，用 linked CTS 保留该期限，并区分用户取消与超时。无需期限的宿主也应传递实际生命周期 token。
- `CompletionUsage` 的 `null` 表示未知，`0` 是明确报告的零；不要为了填满 profiler 推算缺失维度。usage 是可观测信息，不进入持久请求身份。

`observer: null` 表示不观察增量，client 仍执行流式调用并聚合结果。收到增量不意味着最终成功；UI 展示和持久化提交分别由宿主负责。

## 4. 请求、历史与 reasoning

`CompletionRequest` 由模型 ID、冻结的 `CompletionPromptPrefix` 和尾部消息组成。SystemPrompt 与普通观察消息分开；工具只从 `PromptPrefix.OutputContract` 进入请求。共享前缀与 tail 显式划分，不在 provider adapter 中猜测消息边界。

下一轮历史使用成功结果的 `CompletionResult.Message`，它实现 `IHistoryMessage`；`CompletionResult` envelope 本身不是历史消息。工具结果的 `ToolCallId + ToolName` 必须与待回复调用一一对应，缺失、重复或错配会在请求构造/投影时失败。

可见正文与 reasoning 原生 payload 分开。保留 `ActionBlock` 中的 Origin、协议身份与 replay payload，不能用 flatten 后的正文替代；Gemini tool continuation 需要它的 replay 信息。Codex Responses 与 public OpenAI Responses 的 `ApiSpecId` 不同，不可交叉回放。跨模型约束见 [Codex adapter 合同](openai-codex-subscription-client-design.md#64-独立-protocol-identity)。

请求合同没有调用者自设 output-token cap；需要显式数值的 provider 使用模型能力规则。`CompletionInvocationOptions.PromptCacheReuseHint` 是 best-effort 的复用提示，不是禁止存储或隐私保证，也不属于请求逻辑身份。

## 5. Tools、日志与验证入口

Tools 独立引用 `Atelia.Completion.Tools`。从 `MethodToolWrapper` 或 `ArtifactToolWrapper<T>` 建立工具，放进 `ToolRegistry`，通过 `registry.CreateSession(...)` 获得 `ToolSession`，用 `VisibleDefinitions` 构造本轮工具合同并调用 `session.ExecuteAsync(...)`。当前不使用旧的 ToolExecutor/ToolSessionState 接口；完整 DTO 示例见 [Tools README](../../src/Completion.Tools/README.md)。

`ToolSession` 面向顺序调用，非线程安全；可见性与执行权限使用同一 Access 快照。校验失败不调用业务方法；执行序号不会自动实现持久化或副作用事务。游戏工具的行动仍应走宿主 Intent 仲裁与世界提交。

Diagnostics 可单独引用，详见 [日志配置](../../src/Diagnostics/README.md)。Release 包中已被裁掉的 Trace/Info 调用不能通过环境变量恢复；需要库内部 Debug 诊断时使用源码 Debug 联调。

源码离线测试入口是 `tests/Completion.Tests/Completion.Tests.csproj`。真实 provider 探针有显式 live 开关，须记录实际 provider/model/platform 和结果；默认未启用的测试与历史 `experiments/` 文件不代表本轮已经执行在线调用。HTTP 录制回放的边界见 [管线文档](http-transport-pipeline.md)，golden log 不是权威持久回执。
