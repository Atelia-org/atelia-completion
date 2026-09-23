# Anthropic 已知模型输出上限直取（2026-09-24）

## 决策与边界

`AnthropicClient` 必须提供 Messages 请求的 `max_tokens`。之前每个客户端实例
首次使用一个模型 ID 时都先查询所配置端点的 `GET v1/models/{id}`，再发送
`POST v1/messages`；成功值按精确 ID 缓存。实测中转站对
`claude-opus-5-5` 的 Models 查询返回 404，但 Messages 请求成功。因此已知模型
的前置查询增加一次往返，还可能因中转站缺少 Models API 阻断可用的 Messages。

现在只对已有表中的五个精确 Claude API ID 直接使用官方标准 Messages 输出上限：
`claude-opus-4-6`、`claude-opus-4-7`、`claude-opus-4-8`、
`claude-opus-5`、`claude-opus-5-5` 均为 128K tokens。该值进入既有的
`ProviderModelMaximumCache`，无需 Models HTTP 请求。Anthropic 的
[模型 ID 规则](https://platform.claude.com/docs/en/about-claude/models/model-ids-and-versions)
说明这些 ID 指向固定模型版本；各模型官方规格：
[4.6](https://platform.claude.com/docs/en/models/opus-4-6/overview)、
[4.7](https://platform.claude.com/docs/en/models/opus-4-7/overview)、
[4.8](https://platform.claude.com/docs/en/models/opus-4-8/overview)、
[5](https://platform.claude.com/docs/en/models/opus-5/overview)、
[5.5](https://platform.claude.com/docs/en/models/opus-5-5/overview)。
批量 API 的 beta 扩展不适用于此处的标准 Messages 请求。

别名、未知 ID 和自定义后缀均继续查询**配置的**端点。成功响应必须提供
合法的正整数 `max_tokens`；仅 404/405/501 使用保守的 32768 回退并缓存。
401/403/429、服务与传输错误、畸形成功响应仍失败。客户端不会携带中转站
凭据向 Anthropic 官方地址作跨站查询；[官方 Models API](https://platform.claude.com/docs/en/api/http/models/retrieve)
也要求 API 凭据。

对宣称使用这些精确 ID、却施加不同输出上限的中转站，本改动会使用官方
上限，而不再采用其 Models API 报告的值；此类非标准路由需要单独评估。
没有更改 `ApiSpecId`、连接指纹算法或公共配置接口。原来从 Models API
得到不同数值的相同连接，升级后发送的 `max_tokens` 会改变。

## 验证

来源基线 commit：`add27d6`。离线测试覆盖：

- 五个已知 ID 各调用两次，只出现 Messages POST，`max_tokens=128000`；
- 未知 ID 成功查询后按精确 ID 缓存该值；别名/自定义后缀仍查询；
- 未知 ID 的 Models 404/405/501 回退为 32768，401/403/429/500/503
  与畸形响应仍阻止 POST；
- 冻结的精确连接绑定继续可执行，已知 ID 无前置查询。

受影响的 `ProviderModelMaximumTests` 与 `AnthropicClientTests` 共 73 项通过。
完整离线套件 858 项通过，1 项 Windows 专属测试在 Linux 跳过。

真实中转站测试使用 `CLAUDE_BASE_URL` 与 `CLAUDE_API_KEY`，没有记录其值。
`claude-opus-5-5` 首轮和同模型续轮均为 Messages HTTP 200、`Completed`，
有正文增量并包含要求的标记。再在私有临时目录运行 raw 录制，JSONL 中
只有两条 HTTP 200 POST，没有 Models GET；两次请求均为
`model=claude-opus-5-5`、`max_tokens=128000`，第二次含一轮 assistant 历史；
响应均声明该模型，并收到 `message_stop`。原始 JSONL 权限为 `0600`，
检查元数据后已删除。最终构建后又运行一次 LiveE2E，首轮和续轮仍通过，
`modelsHttp=none`。三次 LiveE2E 运行合计六次真实生成请求。

此结果证明该中转站当前能够接受已知模型的直取上限并完成基础文本及续轮。
中转站声明的模型字段不能独立证明其内部实际使用的上游模型。
