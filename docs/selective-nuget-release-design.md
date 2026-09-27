# Completion 按包发布落地方案

本仓复用 [atelia-storage 的按包发布方案](https://github.com/Atelia-org/atelia-storage/blob/cedc952/docs/selective-nuget-release-design.md) 的发布合同。本文记录 completion 的实际入口与依赖图。旧 `v<version>` 四包同版发布仍可用；新包使用 `Atelia.<Project>-v<version>` 标签，一次只发布一个包。

| 新包 | 直接仓内依赖 | 单包候选输入 |
| --- | --- | --- |
| `Atelia.Diagnostics` | 无 | `-Project Diagnostics` |
| `Atelia.Completion.Abstractions` | 无 | `-Project Completion.Abstractions` |
| `Atelia.Completion` | Diagnostics、Completion.Abstractions | `-Project Completion -DependencyVersions @{...}` |
| `Atelia.Completion.Tools` | Diagnostics、Completion.Abstractions | `-Project Completion.Tools -DependencyVersions @{...}` |

两个上层包彼此没有依赖，故单发 Tools 不改变 Completion 的版本。先用已公开的底层版本做包模式编译；若使用了旧包缺少的 API，先按依赖顺序发布所需底层新包并完成公开回读，再构建上层。不能以源码构建成功代替这一步。底层单独发版不会自动更新旧上层包的依赖下限。

## 本地候选与验收

在干净的已提交源码树中执行，版本必须是此前未公开的新身份。以下版本仅为示例，实际运行应选择新的 SemVer。候选包和其签名的公开依赖包会被放入一次性 feed；manifest 记录候选来源、SDK、包哈希和每个旧依赖的公开字节与原来源。

```powershell
$version = '0.1.1-dev.2026092801'
$deps = @{
    'Atelia.Diagnostics' = '0.1.0-preview.3'
    'Atelia.Completion.Abstractions' = '0.1.0-preview.3'
}
./eng/Pack.ps1 -Project Completion.Tools -Version $version -DependencyVersions $deps -OutputDirectory ./artifacts/tools-feed
./eng/Test-Package.ps1 -Project Completion.Tools -Version $version -FeedDirectory ./artifacts/tools-feed -WorkDirectory "../completion-tools-smoke-$version"
```

无内部依赖的包不提供 `-DependencyVersions`：

```powershell
$version = '0.1.1-dev.2026092802'
./eng/Pack.ps1 -Project Diagnostics -Version $version -OutputDirectory ./artifacts/diagnostics-feed
./eng/Test-Package.ps1 -Project Diagnostics -Version $version -FeedDirectory ./artifacts/diagnostics-feed -WorkDirectory "../completion-diagnostics-smoke-$version"
```

`Pack.ps1` 要求准确的 SDK、正确 origin、干净 commit、空输出目录及 nuget.org 尚不存在的新版本。上层项目仅在单包模式使用已声明版本的 `PackageReference` 编译；隔离 restore 核查实际引用的包与候选 nuspec 下限。`Test-Package.ps1` 以仓外独立消费项目、NuGet 配置和缓存验收新包的独立入口及解析闭包。旧四包同版入口和 smoke 保留，供现有发布链使用。

## 公开发布

手动 workflow 从包专属 tag 触发，输入项目、版本及上层包的两个直接依赖版本。workflow 构建并运行离线测试，再从同一份候选 feed 做隔离包消费验收，只推送 manifest 中唯一的新 nupkg。上传前冻结候选包哈希及 manifest 到 Actions artifact。`push` 成功后等待 nuget.org 可下载并重新从公开源核对签名包、来源、包资产与消费闭包。公开包经过 nuget.org 仓库签名，整包哈希通常与上传前候选不同；两阶段分别记录。

如果公开回读超时，保留已推送记录和 artifact，稍后单独重跑只读 `eng/Verify-Published.ps1`。不要重跑含 push 的 workflow，也不要以同一 ID/版本重新打包。下游仓库若只用一个 Completion 版本属性，还需要按包版本迁移后才能消费混合版本；发布方的完成不代表下游迁移完成。

本次只升级发布能力，未创建新公开版本。旧包仍是 `0.1.0-preview.3`；下次实际发版时再更新面向使用者的版本示例与固定 tag 链接。
