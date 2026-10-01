# NuGet policy preview 发布操作指引

从目标仓库根目录使用 PowerShell 7.3+、Git、GitHub CLI 和根 `global.json` 的 SDK。
先填好目标仓配置，再按阶段执行模板；push／dispatch 仅在已有发布授权时执行。

## 1. 工具与配置

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$gh = (Get-Command gh -ErrorAction Stop).Source
& $gh auth status
$repo = (& $gh repo view --json nameWithOwner --jq .nameWithOwner).Trim()
$releaseBranch = (& $gh repo view --json defaultBranchRef --jq .defaultBranchRef.name).Trim()
git status --short --branch
git remote -v
git log -5 --oneline
git ls-remote origin "refs/heads/$releaseBranch" 'refs/tags/*'
dotnet --version
Get-ChildItem eng, .github/workflows
```

`gh` 不在 PATH 时先查已有安装或目标仓工具说明，把 `$gh` 设为实际绝对路径，不等于
需要立即新装。认证失败可用 `& $gh auth login --hostname github.com --web`；环境中
`GH_TOKEN`／`GITHUB_TOKEN` 覆盖保存的登录，确认覆盖值失效且决定改用本机登录后才
在当前进程清除。不要执行 `gh auth token` 或让用户粘贴 token。

从发布 workflow 提取 `$workflow`（文件名）、`$environment`、`$nugetUserVariable`；
从 CI 提取 `$ciWorkflow`、`$solution`、`$testProject`、`$testFilter`。
发布分支以用户／仓库约定为准，不能仅因默认分支扩大推送范围。

```powershell
& $gh api "repos/$repo/actions/variables" --jq '.variables[] | {name,value}'
& $gh api "repos/$repo/environments/$environment" --jq '{name,protection_rules}'
& $gh run list -R $repo --workflow $workflow --limit 3 --json databaseId,headSha,status,conclusion,url
```

NuGet policy 匹配 GitHub owner／repo、workflow **文件名**、environment；policy owner
是 NuGet 包所属个人或组织，不能混淆为 GitHub owner。登录变量是个人 NuGet 用户名，
不是组织名或邮箱；scope 允许目标新版本，首次建包还需允许新包。workflow 需要
`id-token: write` 和 `NuGet/login@v1`。GitHub API 不证明 policy 本身有效，本次 login
才是本次证据，已有成功配置不需要每次重建。push 的 `--api-key` 来自 OIDC 临时输出，
不是持久 key。[NuGet 官方说明](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)。

## 2. 未使用版本与包集

检查已有准备提交；符合本次范围且尚未公开的版本可以沿用。否则查询公开版本后选新
preview。按实际 workflow／脚本设置 `$version`、`$project`、`$packageIds`、`$tag`、
`$dependencies`，不猜包名／tag，不机械全发。上层单包明确已公开依赖版本。

```powershell
foreach ($id in $packageIds) {
    try {
        $published = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json").versions
    } catch {
        if ($_.Exception -isnot [Microsoft.PowerShell.Commands.HttpResponseException] -or
            $_.Exception.Response.StatusCode -ne [Net.HttpStatusCode]::NotFound) { throw }
        $published = @() # 其他网络错误不能当作“版本未使用”。
    }
    Write-Host "$id : $($published -join ', ')"
    if ($published -contains $version) { throw "$id/$version 已公开，不可覆盖。" }
}
```

确认远端不存在目标 tag，也没有该版本上传中的 run。已有 tag 时核对 SHA 和阶段，
不 force、不移动。旧依赖 PackageReference 编译才能验证上层依赖下限。

## 3. 准备、预检与准确 SHA 的 CI

更新受影响安装示例、包 README、尚未发布说明和固定 tag 链接；单发不能声称全包集
更新。检查 diff、保留其他改动，提交准备及必要修复，使 Pack 源码干净。

```powershell
$env:OPENROUTER_API_KEY = '' # 若目标 CI 使用此变量
foreach ($line in Get-Content ".github/workflows/$ciWorkflow") {
    if ($line -match '^\s+(ATELIA_RUN_[A-Z0-9_]+):') {
        [Environment]::SetEnvironmentVariable($Matches[1], '0', 'Process')
    }
}
# 不同仓的 opt-in 名称可能不同，按其 CI env 全部关闭。
dotnet build $solution -c Release -t:Rebuild
dotnet test $testProject -c Release --no-build --filter $testFilter
git diff --check
```

需要本地包预检时用唯一 dev 版本，参数按该仓脚本：

```powershell
$devVersion = '0.1.0-dev.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$feed = Join-Path $PWD "artifacts/preflight-$devVersion"
$work = Join-Path ([IO.Path]::GetTempPath()) ('nuget-smoke-' + [Guid]::NewGuid().ToString('N'))
# 常见整包集入口；单包按目标合同加 Project／DependencyVersions。
./eng/Pack.ps1 -Version $devVersion -OutputDirectory $feed
./eng/Test-Package.ps1 -Version $devVersion -FeedDirectory $feed -WorkDirectory $work
```

正式 preview 候选由发布 Actions 生成归档，不把本地构建当作相同字节；相同 SDK
不保证跨平台字节相同。共享构建图的 dotnet 操作按目标仓规定串行。

```powershell
if (git status --porcelain) { throw '请先提交本次准备，发布源码必须干净。' }
$revision = (git rev-parse HEAD).Trim()
git push origin "HEAD:refs/heads/$releaseBranch"
& $gh run list -R $repo --workflow $ciWorkflow --branch $releaseBranch --event push --commit $revision --limit 5 --json databaseId,headSha,status,conclusion,url
```

从 JSON 取得该 SHA 本次 push 的 `$ciRunId`，暂空时只读重查；若该仓 CI 不由 push
触发，使用既有入口。不能用另一 SHA 的成功放行。

```powershell
& $gh run watch $ciRunId -R $repo --interval 15 --exit-status
$ci = (& $gh run view $ciRunId -R $repo --json status,conclusion,headSha | ConvertFrom-Json)
if ($ci.status -cne 'completed' -or $ci.conclusion -cne 'success' -or $ci.headSha -cne $revision) {
    throw '准确 release SHA 的 CI 尚未通过。'
}
```

失败查 `& $gh run view $ciRunId -R $repo --log-failed`；修复后重走准确 SHA 的 CI。
Windows 成功不能掩盖 Linux 专属 fixture 失败；平台 skip 和未执行 live 分别报告。

## 4. 不可变 tag 和一次 dispatch

`$tag` 按目标 workflow 门禁构造并指向已验收 SHA。

```powershell
if ((git rev-parse HEAD).Trim() -cne $revision -or (git status --porcelain)) {
    throw '源码身份变化，请重新验收。'
}
git tag -a $tag -m "Release $version" $revision
git push origin "refs/tags/$tag"
# 常见输入名，先与目标 workflow 对齐。
if ($project -eq 'All') {
    & $gh workflow run $workflow -R $repo --ref $tag -f "version=$version" -f project=All
} else {
    $dependencyJson = ConvertTo-Json -InputObject $dependencies -Compress
    & $gh workflow run $workflow -R $repo --ref $tag -f "version=$version" -f "project=$project" -f "dependency_versions=$dependencyJson"
}
& $gh run list -R $repo --workflow $workflow --branch $tag --event workflow_dispatch --commit $revision --limit 5 --json databaseId,headSha,status,conclusion,url
```

`All` 不传依赖 JSON；常见无依赖单包传 `{}`。JSON 作为单参数传入。仅配置
`workflow_dispatch` 时 push tag 不会发布，必须从匹配 tag 触发。网络报错或列表暂空
先确认是否已创建 run，不盲目重复 dispatch。[CLI 参数](https://cli.github.com/manual/gh_workflow_run)。

取得本次 `$runId` 后：

```powershell
& $gh run watch $runId -R $repo --interval 15 --exit-status
& $gh run view $runId -R $repo --json status,conclusion,headSha,jobs,url
```

核对验证、候选归档、login、push、公开回读；上传顺序以实际 manifest 为准，单包
不能重复上传冻结的旧依赖。

## 5. 接受结果与只读恢复

完成条件是身份正确且公开回读和独立 PackageReference 消费成功，不只是 push。
公开处理有延迟，按目标回读脚本的窗口继续等待。
已确认 push 成功但 flat-container 索引暂未列出新版本时，继续只读回读等待，不重新 dispatch。

```powershell
$run = (& $gh run view $runId -R $repo --json attempt,headSha,status,conclusion | ConvertFrom-Json)
if ($run.headSha -cne $revision) { throw '发布 run 来源 SHA 不匹配。' }
$evidence = Join-Path $PWD "artifacts/release-$version-$runId-$($run.attempt)"
# 从目标 upload-artifact 步骤构造本次 attempt 的名称。
& $gh run download $runId -R $repo -n $candidateArtifact -D (Join-Path $evidence 'candidate')
```

成功时下载 `$publicArtifact`，读取公开报告，核对 ID、版本、SHA、包数和实际闭包。
manifest 保存 SDK／候选 nupkg、snupkg 哈希，公开报告另存签名后哈希。公开包多出
`.signature.p7s`，整包哈希不同正常；回读应逐资产比较、验证签名，并让各消费者从
nuget.org restore／build／run，核对实际缓存字节。

| 到达阶段 | 下一步 |
| --- | --- |
| CI／候选失败，未上传 | 查日志修复；内容变化用新候选身份，重新验收 |
| OIDC login 失败，未 push | 核对实际用户、policy owner／scope／repo／workflow／environment，不改持久 key |
| 部分上传或上传不确定 | 保留 run／候选，逐包查日志和公开状态；停止自动重跑，不移动 tag、不覆盖版本 |
| 全部 push 成功，回读失败／超时 | 从归档候选重跑只读回读，不能重跑含 push 的 workflow |
| workflow／公开回读成功 | 保存证据并交付，不再发布一次 |

只读恢复先取得上面的 run／evidence 并下载候选，没有公开报告时不要求其 artifact。
从目标脚本确定 `$manifestName`，定位 feed 后执行：

```powershell
$manifestFiles = @(Get-ChildItem (Join-Path $evidence 'candidate') -Recurse -Filter $manifestName)
if ($manifestFiles.Count -ne 1) { throw '预期恰有一份 release manifest。' }
$publicWork = Join-Path ([IO.Path]::GetTempPath()) ('nuget-public-' + [Guid]::NewGuid().ToString('N'))
./eng/Verify-Published.ps1 -Project $project -Version $version -FeedDirectory $manifestFiles[0].DirectoryName -WorkDirectory $publicWork
Copy-Item -LiteralPath (Join-Path $publicWork 'published-check.json') -Destination (Join-Path $evidence 'published-check-local.json')
```

参数以目标脚本为准，使用 release 源码版本，每次 WorkDirectory 新建；必要时独立
checkout 保留当前改动。不能重新 Pack 或清全局缓存代替归档。候选 artifact 缺失时
明确报告证据不足，不能声称完成候选／公开内容比对。
