# Atelia Completion 协作入口

主要使用简体中文。本仓维护 Diagnostics、Completion.Abstractions、Completion、Completion.Tools 四个独立包；不依赖原 Atelia 或 atelia-storage 源码目录。保持 `Atelia.*` 包/程序集/namespace 身份与各项目 TFM；Diagnostics 为 `netstandard2.0`，其余为 `net10.0`。不恢复原仓 Style Analyzer 引用。

先读 [README](README.md)、[快速上手](docs/Completion/quick-start.md)和 [传输合同](src/Completion/README.md)，按任务再读 [Tools](src/Completion.Tools/README.md) 或 [Diagnostics](src/Diagnostics/README.md)。提取来源见 [extraction-origin](docs/extraction-origin.md)。历史设计与实验中的旧路径不意味着需要检出原仓。

## 必须保持的边界

- `Completion.Abstractions` 不依赖 Diagnostics；Tools 不依赖 provider 实现。不要为白盒消费添加跨仓 IVT 或扩大 public API。
- 只有 Completed 正文适合进入成功业务路径。Incomplete/Failed 不能冒充成功，terminal 前流中断表示结果不确定，不能透明重试。
- transport 不设置 operation/idle timeout；期限归宿主 CancellationToken，取消保留 caller token。已有宿主 RequestTimeout 应在宿主边界转成 token，不能在适配时丢失。
- usage 的 null 表示未知，零需要实际依据；reasoning 原生 payload 的 Origin 与协议身份必须保持，不能当正文或跨协议任意回放。
- `ToolSession` 顺序使用，非线程安全。参数绑定和权限失败不执行业务方法；执行序号不构成持久化、事务或 exactly-once 保证。副作用提交归宿主。
- Release 库内 Trace/Info 调用可能已被编译裁掉；环境变量不能恢复调用。调试详见 Diagnostics README，不因迁移引入日志框架。

## 验证与交付

使用根 `global.json` 的 SDK；构建、测试、Pack 命令见 README。同一可写构建图的 dotnet 操作串行运行。常规离线测试按 [.github/workflows/ci.yml](.github/workflows/ci.yml) 关闭五个 opt-in 开关和 `OPENROUTER_API_KEY`，并排除 `Category=LiveE2E` 与 `Category=LocalE2E`；标注平台限定测试和未执行项，不把提前返回的 live 测试当作在线成功。

改代码后跑受影响测试；改包元数据或 README 后核验实际 nupkg 与独立 `Test-Package.ps1` 消费者。新候选内容使用唯一版本，记录来源 commit 和实际包哈希；SDK 相同不证明跨平台字节相同。不要覆盖已发布版本，也不要清全局缓存来隐藏来源错误。

若本任务包含公开发布，发布前更新“尚未发布”说明、包内版本示例及固定 tag 链接，再产包；发布后从公开源重新取得完整包集验证。提交、推送和发布范围以当前用户授权为准，本文不授予额外权限。

真实模型调用与离线 fixture 分开报告。按用户已有授权可使用可用的廉价模型验证；凭据只在运行时读取，不写入仓库、测试数据或日志。不要把历史实验 JSONL 当作本次在线验证。
