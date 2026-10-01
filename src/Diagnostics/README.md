# Atelia.Diagnostics

`netstandard2.0` 轻量诊断输出库，不依赖 Completion 或 Tools，也不引入日志框架。

```powershell
dotnet add package Atelia.Diagnostics --version 0.1.0-preview.6
```

```csharp
using Atelia.Diagnostics;

DebugUtil.Debug("Example", "Source-level diagnostic detail.");
DebugUtil.Warning("Example", "A semantic fallback was used.");
DebugUtil.Error("Example", "The library itself failed.");
```

## Public API

| 方法 | 编译行为 | 用途 |
|---|---|---|
| `Debug(string category, string text)` | `[Conditional("DEBUG")]`，Release 调用点零开销 | 开发期 tracing，以及已由结果/异常/工具执行结果携带的重复事实 |
| `Warning(string category, string text)` | 始终保留 | public 结果未携带、但可能影响运行语义或诊断能力的事实 |
| `Error(string category, string text)` | 始终保留 | 库自身真实错误，不用于重复业务失败 |

没有 Exception 参数、类别配置面、`Log`/`Print`/`ClearLog` 或事件种类 API。需要异常信息时，调用方只写 `exceptionType={exception.GetType().FullName}`，不得写入异常消息或堆栈。

## 级别语义

- **Debug**：事实已经由非成功 `CompletionResult`、抛出的异常或工具执行结果完整携带；或是协议明确允许的 forward-compatible / 预期路径。
- **Warning**：成功路径上的降级、回退、容错，或 best-effort 诊断旁路自身失败而主结果被保留。这类事实默认应该可见。
- **Error**：库自身真实错误。业务失败已由结果或异常报告时，不再重复打印 Error。

内容规则按级别分层：

- **Warning / Error**：调用文本必须 content-free——不得包含凭据、provider 原文、正文、文件路径或堆栈。这两类日志可能出现在任何部署形态的控制台上，只承载事实摘要（如 `exceptionType={type.FullName}`）。
- **Debug**：开发期临时诊断，可以记录宿主自己的数据（预览文本、内部状态、路径等）。`[Conditional("DEBUG")]` 保证 Release 消费者不会执行这些调用；输出卫生由 `DebugUtil` 的控制字符单行化和固定长度截断保证。

## 配置

环境变量在首次使用 `DebugUtil` 前读取一次，值大小写不敏感、无别名：

| 变量 | 接受值 | 默认 |
|---|---|---|
| `ATELIA_DEBUG_FILE_LEVEL` | `DEBUG` / `WARNING` / `ERROR` / `OFF` | `DEBUG` |
| `ATELIA_DEBUG_CONSOLE_LEVEL` | `DEBUG` / `WARNING` / `ERROR` / `OFF` | `WARNING` |

非法值回退默认值。`ATELIA_DEBUG_CATEGORIES` 与 `ALL` 已删除；category 只是日志行内标签和文件分区名，不再构成配置面或控制台筛选器。

两个 sink 角色不同，默认值分开：文件 sink 是留档记录，默认 `DEBUG`——Debug 调用点一旦编译存在即落盘；控制台是实时视图，默认 `WARNING` 以保持安静。默认值固定，不依赖 Diagnostics 包自身的 `#if DEBUG`：门控来自消费方调用点的 `[Conditional("DEBUG")]`，Release 消费者调用点被编译期裁掉，文件默认 `DEBUG` 对其严格 no-op。下游使用 Debug 编译不会改变已发布包的 sink 默认级别。

## 输出合同

- 控制台输出一律写 **stderr**，正常路径保持 stdout 干净。
- 文件路径为当前工作目录下 `.atelia/debug-logs/{safe-category}.log`；目录在首次有满足阈值的写入时创建。
- 文件 sink 是 best-effort：无锁、无 rotation、无跨进程一致性，写入失败丢弃该条；不能作为审计回执，多进程可能交错。
- 每条日志一行，UTC ISO-8601 时间戳（如 `2026-09-21T08:15:32.123Z`），文本最长 2048 字符，超长截断。
- category 会单行化、截断并安全化为文件名，避免 public 字符串成为路径注入面。
- 格式化和写入整体 best-effort；诊断失败不得影响 provider、transport 或工具结果。

## Release 与 Debug 调用点

`Debug` 的 `[Conditional("DEBUG")]` 决定调用点是否生成。Completion / Tools 以 Release 打包后，其内部 Debug 调用已被裁掉，环境变量不能恢复；需要源码 Debug 联调时请使用 Debug 构建的库源码。下游自己的 Debug 调用点遵循同一规则。

实现见 [DebugUtil.cs](DebugUtil.cs)。本包使用 MIT 许可证。
