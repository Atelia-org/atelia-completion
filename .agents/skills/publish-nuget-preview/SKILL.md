---
name: publish-nuget-preview
description: "使用目标 .NET 仓库既有打包脚本和 GitHub Actions NuGet Trusted Publishing policy，准备、发布并公开回读验证 preview 包；也支持上传后的只读验收恢复。适用于采用 release tag 和候选 manifest 的 NuGet 发布仓库。"
---

# 发布 NuGet preview

先读目标仓库的 `AGENTS.md`、README、`eng` 发布说明、CI／发布 workflow 和相关脚本，
再读 [操作指引](references/runbook.md)。从**目标仓库根目录**操作，不从 skill 的位置
推断发布目标。复用仓库已有发布链，不另建发布器。

## 提取本次配置

从当前源码和远端确认 repo／分支、SDK、包 ID／TFM、版本／包集、workflow 文件名与
输入、tag 规则、environment、NuGet 登录变量、依赖版本、manifest／artifact 名称、
CI 与公开回读入口。它们是执行变量，不能照搬另一个仓库或历史样例。

runbook 使用常见 `eng/Pack.ps1`、`Test-Package.ps1`、`Verify-Published.ps1` 和
`version/project/dependency_versions` 输入举例；实际参数以目标仓为准。

## 执行顺序

1. **确定身份。** 检查工作区、HEAD、origin、已有准备提交、远端 tag 和 nuget.org。
   沿用符合本次范围且未使用的准备版本；区分整包集和单包，单包冻结已公开依赖。
   不能用源码引用构建成功掩盖旧依赖包缺少 API。
2. **核对授权与认证。** 用户要求公开发布时，按已授权范围完成必要准备、提交、推送、
   tag、dispatch，不重复询问。只要求文档、候选或审查时不触发发布。GitHub 身份用于
   仓库操作；NuGet 临时凭据由 Actions 的 `NuGet/login@v1` 经 OIDC policy 获取。
   核对当前 environment／variable／近期登录记录，不照搬旧会话的阻塞；不索取 key，
   不改成持久 key 或 GitHub Packages。凭据只在运行时使用，不进入文件、日志或对话。
3. **验收准确 commit。** 更新受影响的包内版本示例、尚未发布说明和固定 tag 链接。
   保留用户改动；源码干净且已提交后才 Pack。本地预检用唯一 dev 版本，正式 preview
   候选由 Actions 生成并归档；两次构建不是同一份字节证据。按该仓 CI 关闭 live 开关，
   遵守构建并发边界。等待**该 SHA** 全部要求平台的 CI 成功，不能拿其他 SHA 放行。
4. **准确 tag dispatch 一次。** tag 按目标 workflow 构造并指向已验收 SHA。
   检查触发条件：只有 `workflow_dispatch` 时，push tag 本身不发布。传入包集和依赖，
   获取本次 run ID，监控结束；不因列表暂空或网络报错盲目重复触发。
5. **接受公开结果。** 分开检查 login、push、回读。下载本次 attempt 的候选 manifest
   和公开报告，核对 ID、版本、SHA、包数及闭包。需要本地复核时从归档候选调用只读
   回读入口，不能重新 Pack。push 成功但公开下载尚未就绪时继续等待，不能提前报完成。

## 停止条件与交付

已有任何包上传，或不能证明上传未发生时，停止自动重试含 push 的 workflow；从归档
候选继续只读回读。部分上传逐包报告，不覆盖同 ID／版本、不移动旧 tag。身份或 policy
确实缺失时报告失败阶段和具体配置项，不能把认证失败说成包验证失败。

公开验收验证仓库签名、候选／公开资产与独立 PackageReference 消费闭包。签名会改变
整包哈希，候选和公开哈希分别保留。报告包 ID／版本、commit／tag、Actions URL、源码
与消费者验证、平台 skip 和未执行 live；以实际证据接受结果，不仅凭 workflow 状态。
