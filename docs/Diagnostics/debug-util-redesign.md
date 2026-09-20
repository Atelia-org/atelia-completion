# Atelia.Diagnostics DebugUtil 重设计（已确认，未实施）

状态：设计已确认；**尚未实施，未修改任何库代码**。决策人：仓库所有者（2026-09-21）。
评审方式：三角色辩证评审（需求怀疑者 / 最小架构师 / 语义守护者，两轮独立论点与交叉质询，全部主张经主线程源码证据核实）。
待处理项见 [pending-issues.md](pending-issues.md)。实施时同步重写 [src/Diagnostics/README.md](../../src/Diagnostics/README.md)，使其与代码一致。

## 1. 背景与证据

争议起点：`DebugUtil.ShouldWriteToConsole` 中 `level >= Warning` 无条件绕过类别过滤直达控制台，导致所有消费方默认被运行期 Warning 噪声污染。

已核实的关键证据：

- git 历史：当前形状来自 `c60d7ab`（"保留旧接口兼容性"接口重写）。重写前的实现是"始终写文件 + 仅类别启用时写控制台"，注释原文为"仅在类别启用时输出到控制台，避免干扰单元测试视线"。即：旁路是重写引入的意图回退，不是长期产品法则。
- 文档与代码矛盾：README 称 Warning/Error"仍受 sink 级别和控制台类别控制"，代码实际绕过类别；README 类别示例 `TypeHash,Test,Outline` 已不存在。
- 默认值依赖 Diagnostics 包自身 `#if DEBUG` 对消费者无效：包以 Release 构建，下游 Debug 编译不会改变 sink 默认行为。新设计必须使用固定默认值。
- 调用点普查（`rg "DebugUtil\." src`）：共 58 处 = Trace 11 / Info 17 / Warning 29 / Error 1。`DebugCategory="Provider"` 以私有 const 散落在 14 个文件。
- `DebugUtil` 混用三种角色：开发期 tracing、运行期故障告警、best-effort 文件留痕；且正滑向小型日志框架（EventKind、raw HTTP sink、异常序列化），与"不引入日志框架"的仓库边界冲突。
- content-free 违规：Anthropic/Gemini provider error 直接写入 provider 原文；`FormatMessage` 附加完整 `exception.ToString()`；公开的 `DebugCompletionHttpExchangeSink` 输出 raw request/response/error。

## 2. 已确认决策

1. **控制台默认 `Warning+`，输出到 `stderr`**（先完成 Warning 语义清理；显式 `OFF` 可关）。
2. **删除 `ATELIA_DEBUG_CATEGORIES` / `ALL`**；类别只作为文件分区名与日志行内标签，不再构成配置面。
3. **记录并延后处理**：六处 provider fail-closed 变更、clean-EOF 事实写入 `CompletionTermination.Detail`，见 [pending-issues.md](pending-issues.md)。
4. **Galatea 仓外确认由所有者另行处理**，不阻塞本设计（草稿包无兼容包袱）。

## 3. 目标模型

### 3.1 Public API

```csharp
public static class DebugUtil {
    [Conditional("DEBUG")]
    public static void Debug(string category, string text);
    public static void Warning(string category, string text);
    public static void Error(string category, string text);
}
```

- 删除：public `Log`、`Print`、`ClearLog`、`DebugEventKind`、public `DebugLevel`、Exception 参数。
- `DebugUtil` 不接收 `Exception`。需要异常信息时，调用点只写 `exceptionType={exception.GetType().FullName}`。

### 3.2 内部级别与配置

```csharp
internal enum DebugLevel { Debug = 0, Warning = 1, Error = 2, Off = 3 }
```

| 配置 | 接受值（大小写不敏感，无别名） | 默认 |
|---|---|---|
| `ATELIA_DEBUG_FILE_LEVEL` | `DEBUG` / `WARNING` / `ERROR` / `OFF` | `WARNING` |
| `ATELIA_DEBUG_CONSOLE_LEVEL` | `DEBUG` / `WARNING` / `ERROR` / `OFF` | `WARNING` |

- 默认值固定，不依赖 `#if DEBUG`。
- 删除 `ATELIA_DEBUG_CATEGORIES`、`ALL`、`TRACE`、`INFO` 及 `TRC/WRN/ERR` 等别名。

### 3.3 输出规则

```text
file:    level >= _fileLevel
console: level >= _consoleLevel
```

- 控制台一律写 `Console.Error`，写入与格式化路径整体 best-effort catch；诊断失败不得影响 provider outcome。
- 文件懒创建 `.atelia/debug-logs/{category}.log`：首次满足阈值的写入时创建目录；删除 `gitignore/debug-logs` 与 CWD 两层 fallback；写入失败丢弃该条；无锁、无 rotation，明示非审计回执、多进程可能交错。
- 时间戳使用 UTC ISO-8601（如 `2026-09-21T08:15:32.123Z`），不用本地 `HH:mm:ss.fff`。
- 文本统一单行化：控制字符替换、固定长度截断、content-free（不得含凭据、provider 原文、正文、路径）。
  现有 `FormatMessage` 先格式化后判定 sink 的问题一并修正（先判定，后格式化）。
- category 用于文件名时必须安全化（替换路径分隔符等），避免 public 字符串成为路径注入面。

### 3.4 类别权威

各程序集内部常量，不进入 public API、不进入 Diagnostics 包：

- Completion：`Provider`、`Completion.CallLog`、`Completion.HttpCapture`
- Tools：`Tools`

## 4. 级别语义法则（写入 README）

- **Debug**：该事实已由非成功 `CompletionResult`、抛出的异常或工具执行结果完整携带；或协议明确允许的 forward-compatible/预期路径。
- **Warning**：public 结果没有携带、但可能影响运行语义或诊断能力的事实。主要是：①成功路径上的降级/回退/容错（capability fallback、clean-EOF 兼容、replay 参数回退、reasoning 投影 mismatch）；②best-effort 诊断旁路自身失败（call-log、HTTP capture、cleanup 失败，而主结果被保留）。
- **Error**：库自身真实错误，默认 stderr 可见；不用于重复业务失败。
- 不因"调用失败了"就自动 Warning；失败事实已在结果或异常中时，重复打印只是噪声。

## 5. 调用点迁移矩阵（29 Warning + 1 Error）

### 降为 Debug（12 处：事实已由权威通道携带）

| 位置 | 语义 | 权威通道 |
|---|---|---|
| [ToolDispatch.cs:36](../../src/Completion.Tools/ToolDispatch.cs) Forbidden | 工具被拒 | `ToolExecuteResult` Failed |
| [ToolDispatch.cs:46](../../src/Completion.Tools/ToolDispatch.cs) Missing tool | 工具缺失 | 同上 |
| [ToolDispatch.cs:72](../../src/Completion.Tools/ToolDispatch.cs) Cancelled | 执行取消 | Skipped 结果 |
| [ToolDispatch.cs:94](../../src/Completion.Tools/ToolDispatch.cs) Failed（现 Error） | 工具异常 | Failed + 异常详情在结果 |
| [AnthropicStreamParser.cs:439](../../src/Completion/Anthropic/AnthropicStreamParser.cs) API error | provider 失败 | `CompletionResult` Failed + Errors |
| [GeminiStreamParser.cs:359](../../src/Completion/Gemini/GeminiStreamParser.cs) API error | 同上 | 同上 |
| [OpenAIChatStreamParser.cs:97](../../src/Completion/OpenAI/OpenAIChatStreamParser.cs) provider error | 同上 | 同上（保留固定文本工厂供测试） |
| [AnthropicStreamParser.cs:446](../../src/Completion/Anthropic/AnthropicStreamParser.cs) Unknown event | forward-compatible 忽略 | 传输合同明确非失败 |
| [OpenAIResponsesStreamParser.cs:259](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs) unfinished reasoning | terminal 未收口 | `MarkIncomplete(detail)` |
| [OpenAIResponsesStreamParser.cs:268](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs) unfinished function calls | 同上 | 同上 |
| [AnthropicStreamParser.cs:460](../../src/Completion/Anthropic/AnthropicStreamParser.cs) unfinished content blocks | 同上 | 同上 |
| [AnthropicClient.cs:274](../../src/Completion/Anthropic/AnthropicClient.cs) transport read failure | 读失败 | `CompletionFailureException` 已抛出 |

### 保留 Warning（18 处）

| 组 | 位置 | 备注 |
|---|---|---|
| capability fallback | [AnthropicClient.cs:369](../../src/Completion/Anthropic/AnthropicClient.cs) | 成功结果掩盖的保守 max_tokens |
| clean-EOF 兼容 | [AnthropicClient.cs:215](../../src/Completion/Anthropic/AnthropicClient.cs) | 见 pending-issues：拟写入 `CompletionTermination.Detail` 后降级 |
| replay 参数回退空对象 ×4 | [AnthropicMessageConverter.cs:431](../../src/Completion/Anthropic/AnthropicMessageConverter.cs)、[:437](../../src/Completion/Anthropic/AnthropicMessageConverter.cs)、[GeminiMessageConverter.cs:345](../../src/Completion/Gemini/GeminiMessageConverter.cs)、[:351](../../src/Completion/Gemini/GeminiMessageConverter.cs) | 见 pending-issues：拟 fail closed |
| reasoning switch/mismatch ×2 | [OpenAIResponsesStreamParser.cs:590](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs)、[:632](../../src/Completion/OpenAI/OpenAIResponsesStreamParser.cs) | 见 pending-issues：拟 fail closed |
| cleanup 失败 ×8 | [AnthropicClient.cs:443](../../src/Completion/Anthropic/AnthropicClient.cs)、[:454](../../src/Completion/Anthropic/AnthropicClient.cs)、[GeminiClient.cs:232](../../src/Completion/Gemini/GeminiClient.cs)、[:243](../../src/Completion/Gemini/GeminiClient.cs)、[OpenAIChatClient.cs:262](../../src/Completion/OpenAI/OpenAIChatClient.cs)、[:273](../../src/Completion/OpenAI/OpenAIChatClient.cs)、[OpenAIResponsesProtocolClientCore.cs:180](../../src/Completion/OpenAI/OpenAIResponsesProtocolClientCore.cs)、[:191](../../src/Completion/OpenAI/OpenAIResponsesProtocolClientCore.cs) | 次级诊断失败本身不可见；文本改 exception-type-only |
| 诊断 sink 失败 ×2 | [LoggingCompletionClient.cs:276](../../src/Completion/LoggingCompletionClient.cs)、[CompletionHttpClientBuilder.cs:154](../../src/Completion/Transport/CompletionHttpClientBuilder.cs) | 保持 content-free |

另有 28 处 Trace/Info 机械迁移为 `Debug`（11 + 17）。

## 6. 明确不做 / 推迟（含触发条件）

- 不引入 MEL/Serilog/NLog 等日志框架。
- 不做全局宿主编程式配置、进程内事件钩子、逐测试捕获 API（触发：真实宿主排障记录）。
- 不做跨进程文件锁、rotation（触发：真实多进程审计需求；文件明示非审计回执）。
- 不做 per-category 级别矩阵（触发：真实排障记录证明需要）。
- 不让 Warning/Error 受类别门控；也不重建类别选择器（触发：真实源码联调需要按类别收窄控制台 Debug 输出）。
- 不做"文件写失败时限频 stderr guard"新机制：默认 Warning+ stderr 已覆盖磁盘满场景，无需新增机制。

## 7. 实施切片与验收

1. **Diagnostics 核心**：重写 `DebugUtil`（三级模型、内部枚举、固定默认值、stderr、UTC ISO、单行有界、懒创建、全路径 best-effort、先判定后格式化）；重写 README 为真实合同。
2. **机械迁移**：28 处 Trace/Info → `Debug`；删除所有 Exception 实参（改 exception-type-only）；集中类别常量。
3. **Warning 审计落地**：按第 5 节矩阵执行 29+1 处定级；同步调整依赖诊断文本/工厂的测试（如 `OpenAIChatStreamParserTests` 的固定文本断言）。
4. **public 面收缩**：删除 `Log`/`Print`/`ClearLog`/`DebugEventKind`/`DebugCompletionHttpExchangeSink`；保留 `ICompletionHttpExchangeSink`、InMemory 与文件 sink、`callLogFailureReporter` 注入缝。
5. **验证**：按仓库规则串行执行构建、离线测试；因 public API 与 README 变化，须以唯一版本候选包运行 `eng/Test-Package.ps1` 独立消费者验证。

验收判据：

- 默认（无环境变量）Release 消费：正常路径零控制台输出；成功掩盖的回退与诊断旁路失败出现在 stderr；stdout 始终干净。
- 显式 `OFF` 后完全静默；显式 `DEBUG` 后源码 Debug 明细可见。
- Release 构建中 `Debug` 调用点零开销，环境变量不能恢复。
- 模拟 console writer 抛异常、文件目录不可写时，provider outcome 不变。
- 日志文本检索不到凭据、provider 原文、正文、路径、stack。
- `dotnet test` 无需逐宿主降级仍保持低噪声（`TestHostDiagnostics` 作为测试宿主政策保留）。
