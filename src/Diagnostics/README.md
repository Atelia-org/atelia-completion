# Atelia.Diagnostics

`netstandard2.0` 通用调试库，可单独引用；不依赖 Completion 或 Tools。首次公开目标为 `0.1.0-preview.1`，目前尚未发布，先使用明确版本本地候选包。发布前更新此状态。

```csharp
using Atelia.Diagnostics;

DebugUtil.Warning("Example", "Example warning");
```

## DebugUtil 配置
- 推荐优先使用 `DebugUtil.Trace/Info/Warning/Error` 输出调试信息；其中 `Trace/Info` 带 `[Conditional("DEBUG")]`，Release 默认零调用开销。
- `DebugUtil.Print("类别", "内容")` 仅保留为旧调用兼容入口，后续建议迁移。
- 通过设置环境变量 `ATELIA_DEBUG_CATEGORIES` 控制哪些类别的调试信息输出，多个类别用逗号或分号分隔，如：`TypeHash,Test,Outline`。
- 设置 `ATELIA_DEBUG_CATEGORIES=ALL` 可输出所有类别到控制台。
- `ATELIA_DEBUG_FILE_LEVEL` / `ATELIA_DEBUG_CONSOLE_LEVEL` 可分别覆盖文件与控制台最小级别；默认 `DEBUG` 为 `Trace+`，`RELEASE` 为 `Warning+`。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关和后续维护。
- 文件默认目录为当前工作目录下 `.atelia/debug-logs/{category}.log`，不可用则回退 `gitignore/debug-logs/`，两者均不可用时使用当前目录；文件写入失败被吞掉，不能视为可靠审计回执。
- 类别开关只控制控制台输出；文件输出由文件最小级别决定。环境配置在 DebugUtil 静态初始化时读取，应在首次调用前设置。
- `DebugUtil.ClearLog("类别")` 可清空该类别日志。

## Release 包与源码 Debug

`Trace/Info` 的 Conditional 决定调用点是否生成。Completion/Tools 按 Release 编译时，其内部这些调用已经被裁掉；下游 Debug 编译或设置环境变量都不能恢复，需使用库源码 Debug 联调。

下游自己的 Debug 调用点可以保留 Trace/Info，但已发布 Diagnostics 的默认 sink 级别仍由 Diagnostics 自身构建配置决定。要显示这些调用，首次使用前同时设置类别与 `ATELIA_DEBUG_FILE_LEVEL=Trace` / `ATELIA_DEBUG_CONSOLE_LEVEL=Trace`。Warning/Error 与显式 `Log` 没有 Conditional 裁剪，仍受 sink 级别和控制台类别控制。

实现见 [对应版本源码](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/src/Diagnostics/DebugUtil.cs)；首次发布前 tag 链接可能尚不可访问。本包使用 MIT 许可证。
