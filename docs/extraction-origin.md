# 提取来源

2026-09-14 从 `https://github.com/Atelia-org/atelia` 的
`c66d84654408321aab00a66aeb53e1dd19a44679` 在独立 fresh clone 提取。
原仓启动时已有 AGENTS.md 修改及未跟踪的拆仓方案/审查文档，均保留在原仓；这些文件不属于库源码输入。

使用 git-filter-repo 2.47.0，保留 185 个相关提交，过滤后 main 为
`761d377041886b04237dbc885410fd0b56ba7ebb`。过滤会改变 commit ID，不修改原仓历史。
完整映射见 [extraction-commit-map.txt](extraction-commit-map.txt)。

提取路径：

```text
src/Diagnostics/
src/Completion.Abstractions/
src/Completion/
src/Completion.Tools/
tests/Completion.Tests/
docs/Completion/
prototypes/Completion/
prototypes/Completion.Abstractions/
prototypes/Completion.Tools/
prototypes/LlmProviders/
prototypes/LlmProviders.Abstractions/
```

当前六目录共 169 个文件；四库共 96 个 C# 文件。保留独立库阶段历史，
不承诺追溯更早混居 Agent.Core/LiveContextProto 的每个文件。原仓仍保留完整历史。
LICENSE、.editorconfig、.gitattributes 从同一来源复制；独立构建与交付配置在提取后新增。

运行时代码保持来源行为。唯一预期 C# 迁移差异是取消对
`Atelia.SessionJournal.RecapGrid.Runtime.Tests` 的跨仓 InternalsVisibleTo；
自有 Completion.Tests 仍保留白盒测试权限。原消费者的 public HTTP 投影测试改写与切包另行验收。
