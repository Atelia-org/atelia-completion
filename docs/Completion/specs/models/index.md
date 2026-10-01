# 模型规格调查索引

本目录逐步积累模型能力与参数映射的证据。条目记录核查日期、精确 model-id、适用协议、官方来源、已执行实测与未验证范围；框架舍入政策和厂商事实分开说明。调查文档的存在不表示运行时已经内建该模型，当前接入状态以源码和对应设计为准。

## 已有模型调查

| 模型 | 文档 |
| --- | --- |
| Claude Opus 5.5 | [claude-opus-5-5](claude-opus-5-5.md) |
| DeepSeek V4.1 Flash，API ID deepseek-flash | [deepseek-v4-1-flash](deepseek-v4-1-flash.md) |
| GLM-5.3 | [glm-5.3](glm-5.3.md) |
| GPT-6 Astra | [gpt-6-astra](gpt-6-astra.md) |

## Gemini

共同字段、命名边界与已归档实测见 [Gemini Generate Content 规格说明](gemini-generate-content.md)。各模型记录以 2026-10-01 为核查日期。当前源码内建下表前七款现代文本模型的短 ID 与 models/ 形式；其他条目是调查记录。本轮另经生产客户端验证 3.5 Flash-Lite 与 3.8 Flash，和先前原生探针分别归档。

| 精确 model-id | 调查范围 | 该次在线证据 |
| --- | --- | --- |
| [gemini-3.8-flash](gemini-3.8-flash.md) | 现代文本；Stable | 原生 GET、low 成功、minimal 拒绝；生产客户端 low 工具与签名续轮 |
| [gemini-3.7-flash](gemini-3.7-flash.md) | 现代文本；Stable | 无，仅官方文档 |
| [gemini-3.6-flash](gemini-3.6-flash.md) | 现代文本；Stable | 无，仅官方文档 |
| [gemini-3.5-flash](gemini-3.5-flash.md) | 现代文本；Stable | 无，仅官方文档 |
| [gemini-3.5-flash-lite](gemini-3.5-flash-lite.md) | 现代文本；Stable | 原生 GET、low 与省略控制成功；生产客户端 Disabled→low 短题 |
| [gemini-3.1-flash-lite](gemini-3.1-flash-lite.md) | 现代文本；Stable | 仅资源 GET |
| [gemini-3.1-pro-preview](gemini-3.1-pro-preview.md) | 现代文本；Preview | 无，仅官方文档 |
| [gemini-3-flash-preview](gemini-3-flash-preview.md) | 现代文本；较早 Preview 对照 | 无，仅官方文档 |
| [gemini-2.5-pro](gemini-2.5-pro.md) | 预算与访问状态，其他能力未完整整理 | 无，仅官方文档 |
| [gemini-2.5-flash](gemini-2.5-flash.md) | 预算与访问状态，其他能力未完整整理 | 无，仅官方文档 |
| [gemini-2.5-flash-lite](gemini-2.5-flash-lite.md) | 预算与访问状态，其他能力未完整整理 | 无，仅官方文档 |
| [gemini-3.1-flash-lite-image](gemini-3.1-flash-lite-image.md) | 图像变体；限制族规则泛化的反例 | 无，仅官方文档 |

## 后续记录方式

优先以精确 model-id 建立独立 Markdown 条目；名称相近、官方页面列在同一 Versions 下或代理提供别名，都不自动证明能力相同。适用协议与服务面写在条目内；同名模型在不同服务面存在差异时分别记录，不扩展一条结论的适用范围。

未核查的字段明确标为未知，不从同族继承。日期表示实际核查或实测时间；保留来源链接，将网络调用、离线 fixture 和历史实验分别报告。共同协议事实链接到共享说明，后续实测证据另建带日期的文件。这里的 Markdown 是人工维护的证据库，不由运行时读取或自动联网更新。
