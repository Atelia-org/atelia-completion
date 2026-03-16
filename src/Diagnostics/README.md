# Atelia.Diagnostics.DebugUtil 用法说明
- 推荐优先使用 `DebugUtil.Trace/Info/Warning/Error` 输出调试信息；其中 `Trace/Info` 带 `[Conditional("DEBUG")]`，Release 默认零调用开销。
- `DebugUtil.Print("类别", "内容")` 仅保留为旧调用兼容入口，后续建议迁移。
- 通过设置环境变量 `ATELIA_DEBUG_CATEGORIES` 控制哪些类别的调试信息输出，多个类别用逗号或分号分隔，如：`TypeHash,Test,Outline`。
- 设置 `ATELIA_DEBUG_CATEGORIES=ALL` 可输出所有类别到控制台。
- `ATELIA_DEBUG_FILE_LEVEL` / `ATELIA_DEBUG_CONSOLE_LEVEL` 可分别覆盖文件与控制台最小级别；默认 `DEBUG` 为 `Trace+`，`RELEASE` 为 `Warning+`。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关和后续维护。
- 详见 src/Diagnostics/DebugUtil.cs。
