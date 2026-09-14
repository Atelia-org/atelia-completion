# Atelia.Completion.Abstractions

`net10.0` 的模型调用合同：请求与共享前缀、历史消息、工具声明、完成状态、usage 和 provider 来源描述。它不依赖 Diagnostics、Tools 或 provider 实现。

首次公开目标为 `0.1.0-preview.1`，目前尚未发布，先使用明确版本本地候选包。发布前更新此状态。本包使用 MIT 许可证。

- `ICompletionClient.StreamCompletionAsync` 接收 `CompletionRequest`、observer 与调用者 CancellationToken。
- 只有 `Completed` 结果正文进入成功业务处理；Incomplete/Failed 与终止前结果不确定分开处理。observer 增量不代表成功。
- `CompletionResult.Message` 是可回灌的 `ActionMessage`；envelope 本身不是历史消息。reasoning payload 保留原 Origin 与协议身份，不能用正文替代。
- `CompletionUsage` 的 null 表示未知，不能为了统计填零。usage 不进入持久请求身份。
- 工具仅由 `PromptPrefix.OutputContract` 声明；工具结果须按调用 ID 和工具名一一对齐。执行与副作用责任归实现和宿主。

[对应版本快速上手](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/docs/Completion/quick-start.md)包含完整 public client 示例；具体客户端来自另一个 `Atelia.Completion` 包。首次发布前 tag 链接可能尚不可访问。
