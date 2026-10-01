# Completion 发布配置

流程使用仓库 skill [publish-nuget-preview](../.agents/skills/publish-nuget-preview/SKILL.md)
及其 [runbook](../.agents/skills/publish-nuget-preview/references/runbook.md)。skill 可复制到采用
同类 NuGet policy 发布链的其他仓库；本文件记录本仓参数，执行时仍核对当前源码和远端。

## 本仓参数

| 配置 | 值／来源 |
| --- | --- |
| GitHub repo／发布分支 | `Atelia-org/atelia-completion`／`main` |
| SDK | 根 `global.json`：`10.0.201` |
| solution／测试项目 | `Atelia.Completion.slnx`／`tests/Completion.Tests/Completion.Tests.csproj` |
| CI／发布 workflow | `ci.yml`／`publish.yml` |
| 离线过滤 | `Category!=LiveE2E&Category!=LocalE2E`；按 CI env 关闭全部 opt-in 和 `OPENROUTER_API_KEY` |
| 发布触发 | 仅 `workflow_dispatch`，push tag 不自动发布 |
| NuGet policy 名称／owner | `atelia-completion-publish`／NuGet 组织 `Atelia` |
| policy 的 GitHub owner／repo | `Atelia-org`／`atelia-completion` |
| policy 的 workflow／environment | `publish.yml`（仅文件名）／`nuget` |
| Actions 登录变量 | `NUGET_USER=Robird`，个人 NuGet 登录名；不是组织名 |
| OIDC 登录 | `id-token: write`、`NuGet/login@v1`，上传使用其临时输出 |

## 包集、输入与 tag

| project 输入 | 新包 ID | tag | dependency_versions |
| --- | --- | --- | --- |
| `All` | 四包同版 | `v<version>` | 不传 |
| `Diagnostics` | `Atelia.Diagnostics` | `Atelia.Diagnostics-v<version>` | 空或 `{}` |
| `Completion.Abstractions` | `Atelia.Completion.Abstractions` | `Atelia.Completion.Abstractions-v<version>` | 空或 `{}` |
| `Completion` | `Atelia.Completion` | `Atelia.Completion-v<version>` | 两个已公开直接依赖版本 |
| `Completion.Tools` | `Atelia.Completion.Tools` | `Atelia.Completion.Tools-v<version>` | 两个已公开直接依赖版本 |

直接依赖 JSON 的键为 `Atelia.Diagnostics` 和 `Atelia.Completion.Abstractions`。
依赖合同见 [按包方案](../docs/selective-nuget-release-design.md)。四包上传顺序为
Diagnostics、Abstractions、Completion、Tools；单包不重推旧依赖。
TFM：Diagnostics 为 `netstandard2.0`，其余为 `net10.0`。

四包模式变量示例，版本须先查询 nuget.org 后选择：

```powershell
$workflow = 'publish.yml'
$ciWorkflow = 'ci.yml'
$environment = 'nuget'
$nugetUserVariable = 'NUGET_USER'
$solution = 'Atelia.Completion.slnx'
$testProject = 'tests/Completion.Tests/Completion.Tests.csproj'
$testFilter = 'Category!=LiveE2E&Category!=LocalE2E'
$project = 'All'
$packageIds = @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions', 'Atelia.Completion', 'Atelia.Completion.Tools')
$dependencies = @{}
# 设置并验证本次 $version 后：
$tag = "v$version"
```

## 脚本与证据

- `Pack.ps1`：干净 commit、正确 origin、固定 SDK、全新／空 feed。本地预检用唯一 dev
  版本；整包集不传 `-Project`，单包传 `-Project`，上层再传 `-DependencyVersions`。
- `Test-Package.ps1`：候选的独立 public API 消费者，WorkDirectory 必须仓外且全新。
- `Verify-Published.ps1`：公开下载最长等 15 分钟，核对签名、候选资产与消费者闭包；
  可用原候选 feed 和新 WorkDirectory 只读重跑。

上传前归档 `completion-candidate-<runId>-<attempt>`，内有 `completion-feed`、离线
TRX 和候选消费结果。四包 manifest 为 `manifest.<version>.json`，单包为
`manifest.Atelia.<Project>.<version>.json`。公开 artifact 为
`completion-public-check-<runId>-<attempt>`，内含 `published-check.json`。
不要混用别的 run／attempt 的候选与报告。

```powershell
# 取得本次 $runId／$run 后：
$candidateArtifact = "completion-candidate-$runId-$($run.attempt)"
$publicArtifact = "completion-public-check-$runId-$($run.attempt)"
$manifestName = if ($project -eq 'All') {
    "manifest.$version.json"
} else { "manifest.Atelia.$project.$version.json" }
```

候选归档、login、push、公开回读及消费均通过才报告完成。已有任何包上传或不能排除
上传时，不重跑整个 workflow；从归档候选只读恢复。

## 工具线索与已验证样例

本机 `gh` 不在 PATH 时，可检查已有副本
`E:/repos/Atelia-org/.completion-extraction/gh/bin/gh.exe`；只是本机线索，其他机器使用
其实际安装路径。GitHub CLI 登录与 NuGet OIDC 登录是两个阶段。

2026-10-01 四包 `0.1.0-preview.4` 已通过
[发布 run 36826029935](https://github.com/Atelia-org/atelia-completion/actions/runs/36826029935)，
来源 `ecab67c50c4a1b7082d9cfd9a5315067dac01e99`。这是历史参考，不是下次的输入。

同日使用上述 skill，由独立 Luna 子代理完成四包 `0.1.0-preview.5` 的真实发布：
[发布 run 36830080745](https://github.com/Atelia-org/atelia-completion/actions/runs/36830080745)，
来源 `fc8bcc22d1082cbc20dc90575f14f4a30cbc5740`。主线程另从归档候选在 Windows 回读验收，
与 Actions 的 Linux 公开包哈希一致。该次公开处理约需六分钟；push 成功后索引暂未列出
新版本不代表上传失败，应按 15 分钟回读窗口等待，不重复上传。
