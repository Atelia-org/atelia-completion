# Atelia.Diagnostics.DebugUtil 用法说明
- 使用 DebugUtil.Print("类别", "内容") 输出调试信息。
- 通过设置环境变量 ATELIA_DEBUG_CATEGORIES 控制哪些类别的调试信息输出，多个类别用逗号或分号分隔，如：TypeHash,Test,Outline。
- 设置 ATELIA_DEBUG_CATEGORIES=ALL 可输出所有调试信息。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关和后续维护。
- 详见 src/Diagnostics/DebugUtil.cs。
