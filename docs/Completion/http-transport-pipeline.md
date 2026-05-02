# Completion HTTP Transport Pipeline

> **用途**：固定 `prototypes/Completion` 在 HTTP transport 侧的主干设计，避免后续会话在 capture / record / replay 方向上各自发散。
> **适用范围**：`prototypes/Completion/Transport/*` 及所有继续走 `HttpClient` 的 provider client。
> **最后更新**：2026-05-02

---

## 一句话原则

**统一能力放在 `HttpMessageHandler` pipeline，provider 继续只依赖普通 `HttpClient`。**

不要重新发明一套新的 `HttpClient` facade。

---

## 为什么选 handler pipeline

当前 `OpenAIChatClient` 与 `AnthropicClient` 都已经支持注入外部 `HttpClient`。

这意味着：

- capture / record / replay 可以作为 transport 能力统一挂载
- provider 自身不需要知道日志、golden log、mock 回放的具体策略
- 新增 provider 时，只要仍然走 `HttpClient` 路径，就能复用同一套 transport 能力

如果反过来定义一套自制 facade：

- 需要复制 `HttpClient` 的生命周期与配置语义
- 容易演变成第二套半兼容 HTTP 栈
- 与 .NET 生态已有的 `DelegatingHandler` 扩展点重复

因此当前主线是：

1. provider 构造函数继续接收普通 `HttpClient`
2. Completion 侧补一个可装配的 builder / transport layer
3. 具体能力由 handler 链承载

---

## MVP 范围

MVP 只解决三件事：

1. **统一捕获 request / response 文本**
2. **保持 streaming 行为不变**
3. **允许 replay 以替代真实远程服务器**

MVP **不做**：

- byte-level wire capture
- header 级完整法证记录
- 压缩前后字节流区分
- 面向所有任意 `HttpContent` 的零语义损失捕获
- 基于文件格式的 golden log 协议定稿

当前 Completion 里的 body 主要是：

- JSON request
- SSE text response

所以 MVP 统一以 `string` 作为捕获主形态即可。

---

## 主干抽象

### 1. Exchange 记录

一次 HTTP 交换至少记录：

- `Method`
- `RequestUri`
- `RequestText`
- `StatusCode`
- `ResponseText`

在 transport 层于拿到 `HttpResponseMessage` 之前就失败时，还会额外记录：

- `ErrorText`

这里的 `ResponseText` 指的是：**consumer 实际读到并解码出来的文本**。

如果上层提早停止读取流，记录到的文本也允许是部分响应。这是可接受的，因为它仍然忠实反映了本次调用实际消费到的内容。

如果是连接失败、DNS 失败等“尚未得到 response”的异常，则：

- `StatusCode = null`
- `ResponseText = null`
- `ErrorText` 记录异常摘要

### 2. Capture sink

capture 不直接绑定到某一种输出媒介。

统一抽象为 sink，典型用途包括：

- DebugUtil 调试输出
- 内存收集，供单测断言
- 落盘为 golden log

### 3. Replay responder

replay 的职责是：

- 根据 request 视图决定返回哪个 `HttpResponseMessage`
- 完全替代真实远程服务器

MVP 的文件落盘格式固定为 **JSON Lines**：

- 每行一个 `CompletionHttpExchange`
- 字段名使用 camelCase
- 当前不写额外 envelope / version 字段

这保证了：

- 文件可以顺序 append
- 后续 replay 可以逐行读取
- 人工 grep 与审查成本较低

MVP 提供现成的 `JsonLinesCompletionHttpReplayResponder`：

- 按行顺序消费 golden log
- 对当前请求做严格一致性校验
- 将 `responseText` 重建为 `HttpResponseMessage`
- 若记录项带有 `errorText`，则按失败重放并抛出 `HttpRequestException`

---

## 关键约束

### 1. 不破坏 `ResponseHeadersRead + StreamReader` 路径

当前 provider 依赖流式 SSE 读取。

capture 层不能通过“先整体 `ReadAsStringAsync()` 再返还字符串”的方式工作，否则会破坏：

- 渐进解析
- observer 早停
- 长连接 SSE 语义

因此 response capture 必须采用 **tee read stream**：

- provider 继续按原样读取流
- capture stream 在读的同时复制文本内容
- 读取结束或释放时再产出 `ResponseText`

### 2. provider 不感知 capture / replay 细节

provider 只负责：

- 序列化请求
- 调用 `HttpClient.SendAsync(...)`
- 消费返回流

capture / replay / logging 逻辑不应散落到各 provider 内部。

### 3. MVP 以 text-first 为准

当前不为未来可能的二进制 body 预埋复杂分支。

只有当 Completion 真正开始处理：

- 非 UTF-8 body
- 二进制 body
- 精确 byte replay

才升级到底层 bytes-first 模型。

---

## 推荐的代码形态

```text
CompletionHttpClientBuilder
    ├─ CompletionHttpCaptureHandler
    │    └─ tee read stream → CompletionHttpExchange
    ├─ CompletionHttpReplayHandler
    │    └─ ICompletionHttpReplayResponder
    └─ HttpClient
         └─ OpenAIChatClient / AnthropicClient / future providers
```

外部调用方式：

1. 优先使用 `CompletionHttpTransportFactory` 创建 transport setup 或直接创建 `HttpClient`
2. 拿到普通 `HttpClient`
3. 把该 `HttpClient` 注入具体 provider client

保留 `CompletionHttpClientBuilder` 的目的主要是：

- 低层组合测试
- 非标准 handler 链试验
- 后续扩展新的 sink / responder 时有稳定拼装面

对外部调用点，优先走 factory helper。

---

## 演进路线

### Stage 1. 已落地主干

- 统一 capture sink
- 统一 replay responder 接口
- 统一 builder
- 高层 `CompletionHttpTransportFactory` helper
- 显式环境变量开启的本地 round-trip E2E（`ATELIA_RUN_LOCAL_LLM_E2E=1`）

### Stage 2. Golden log 读取与 replay

MVP 已固定写入格式为 JSON Lines；后续再决定：

- 是否补充时间戳 / 标签 / 场景名等附加字段
- request 匹配规则是否需要从“顺序 + 全文本严格校验”升级为按谓词匹配
- 是否需要单独的 envelope / schema version

### MVP Golden Log 示例

```json
{"method":"POST","requestUri":"http://localhost:8000/v1/chat/completions","requestText":"{\"model\":\"gpt-4.1\"}","statusCode":200,"responseText":"data: [DONE]\n"}
```

### Stage 3. 更强 replay

只有出现真实需求后再考虑：

- header 断言
- 多分支匹配
- 半程流中断 / 超时模拟

---

## 非目标提醒

这套 transport pipeline 的目标是：

- 为 Completion 的 provider client 提供一致的文本级 request / response capture
- 为后续 golden log 与 mock replay 预留自然接缝

它**不是**：

- 通用企业级 HTTP 录制框架
- 面向任意协议的抓包器
- 对 `HttpClient` API 的替代品

保持这个边界，能避免后续会话把简单问题做成抽象体操。
