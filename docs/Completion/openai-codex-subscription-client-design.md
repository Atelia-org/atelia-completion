# OpenAI Codex Subscription Client 设计方案

> 状态：Implemented（Borrowed credential MVP；WP-0 至 WP-3 已落地，WP-4 live acceptance 见 §14）  
> 日期：2026-08-25  
> 适用范围：`prototypes/Completion`、`tests/Completion.Tests`，以及选择接入该 client 的 Galatea Host composition  
> 协议声明：代码已经实现不代表 ChatGPT backend 成为稳定公共 API；该 direct route 仍是版本钉死的 implementation coupling

## 1. 结论

新增一个独立的 `OpenAICodexResponsesClient : ICompletionClient`，不要把 ChatGPT OAuth access token 塞进现有
`CompletionConnectionConfig.ApiKey`，也不要把任意动态 header、任意 endpoint 或 OAuth refresh 能力加入
`OpenAIResponsesClient` 的公共参数面。

新 client 与 public OpenAI Responses client 共享 message/tool/reasoning 投影、SSE reader、stream parser 和
aggregator，但拥有独立的：

- credential contract；
- 固定 backend route；
- request headers 与 client identity；
- request-body allowlist；
- `ApiSpecId`、completion surface 与 request-adapter fingerprint；
- 认证失败、redirect 和 rate-limit 语义。

实施分两阶段：

1. **Borrowed credential MVP**：只读 `$CODEX_HOME/auth.json`（默认 `~/.codex/auth.json`）中的当前
   access-token snapshot；Atelia 不读取到运行时 refresh token、不调用 refresh endpoint、不写回文件。
2. **Owned OAuth**：Galatea/Atelia 通过自己的 browser/device login 获得独立 token chain，并使用自己的
   credential store。Pi 与 OpenCode 当前都采用这种独立 ownership；这是长期可靠运行的目标形态。

第一阶段能最快得到一个原生 `ICompletionClient` 实例，并避免 local proxy；它是一个明确版本耦合、需要 Codex
作为唯一 refresh owner 的受控接缝，不是完整 OAuth lifecycle。

## 2. 已验证现状与边界

### 2.1 当前 Atelia Completion

- `OpenAIResponsesClient` 在构造时冻结一个可选 `apiKey`，固定发送相对路径 `v1/responses`。
- `OpenAIResponsesMessageConverter` 已拥有 text、tool call/result、encrypted reasoning replay 和 reasoning effort
  的投影逻辑。
- `OpenAIResponsesStreamParser` 已对 `response.completed`、`response.incomplete`、`response.failed`、`error`
  以及 terminal 前 EOF 建立 fail-closed 语义。
- `CompletionConnectionRegistry` 按 connection 惰性缓存 `ICompletionClient`；这要求 credential provider 在每次
  invocation 获取新 snapshot，不能把 access token 冻结到 client lifetime。
- `connections.json` 是 strict V1：字段语言封闭，但 `kind` 与 `completionSurfaceId` 的值域开放。因此新增 kind
  不要求扩张 V1；新增 `credentialId`、`authFile` 等字段则必须另行设计 V2。
- connection/request-adapter fingerprint 已排除 API key 等 secret。新实现也不得把 token、account id、auth path
  或 token generation 持久化进 dispatch identity。

### 2.2 OpenAI 官方文档边界

OpenAI Docs 确认 Codex 的 ChatGPT login 用于 subscription access，凭证可能位于 `auth.json` 或 OS credential
store，并要求把 file-backed credential 当密码保护：
[Authentication](https://learn.chatgpt.com/docs/auth)。

官方 CI/CD 指南描述的是“让 Codex 自己刷新并保存 `auth.json`”，要求同一份文件只由一台机器或串行 job stream
使用，并明确不把该指南扩展到 generic OAuth clients outside Codex：
[Maintain Codex account auth in CI/CD](https://learn.chatgpt.com/docs/auth/ci-cd-auth)。

官方文档化的产品嵌入面是 Codex App Server；它能拥有 ChatGPT OAuth lifecycle：
[Codex App Server](https://learn.chatgpt.com/docs/app-server)。本方案不采用它，是因为本任务要求保留 Atelia 自己的
harness/context/tool loop，而不是把整个 agent loop 交给 Codex。

### 2.3 版本钉死的实现证据

本次研究使用以下 snapshot：

- 本机 `codex-cli 0.147.0`；对应官方 tag `rust-v0.147.0`，commit
  `be6e8eac029b183056b7e4402879f15d2c85f61b`；
- Pi commit `dcd461925db2edf69a43c8135db1180d418afd54`；
- OpenCode commit `18b4cb6819d7de0b37927fef60d03927e678c9dd`；
- Atelia commit `742fcd62e691b6b6acca4113a3ac3638bc7275ba`。

这些源码共同表明当前 direct SSE route 为
`https://chatgpt.com/backend-api/codex/responses`，使用 ChatGPT OAuth bearer、ChatGPT account id，并发送诚实的
`originator` 与真实 User-Agent。Pi/OpenCode 都拥有自己的 credential store，而不是读写 Codex 的
`~/.codex/auth.json`。

ChatGPT Codex backend 没有公开 wire reference；以上只能作为 pinned implementation evidence。实现应把任何变化
视为 adapter compatibility failure，而不是偷偷回退或伪装官方客户端。

## 3. 为什么选择 sibling client

### 3.1 拒绝“给现有 client 加一个 auth mode 参数”

Codex direct route 的差异不止是 bearer 来源：

- public route 是 `/v1/responses`，Codex route 是 `/backend-api/codex/responses`；
- Codex bearer 必须只能发往一个 pinned HTTPS origin；
- 还需要同一 snapshot 中的 account id；
- `store` 必须固定为 `false`；
- backend model entitlement、reasoning mapping 与 public API catalog 不是同一份契约；
- raw reasoning payload 不应跨 public/Codex surface replay。

若把这些差异压成 `OpenAIResponsesClientOptions` 的 flags、arbitrary headers 和 arbitrary relative path，会形成一个
难以 fingerprint、容易把 subscription token 发往错误 host 的万能入口。

### 3.2 拒绝“只做一个 DelegatingHandler”

`HttpMessageHandler` 适合承载 transport 能力，但它不能独立表达 request-body profile、replay identity、reasoning
mapping 与 capability difference。把 endpoint rewrite 藏在 generic handler 中还会让 durable identity 看不见真实
adapter。

认证注入可以是 sibling client 内部的实现细节，但不能成为跨 provider 的公共 semantic seam。

### 3.3 推荐的共享方式

从现有 `OpenAIResponsesClient` 抽一个 internal shared core/profile seam，保持 public constructor 行为不变：

```text
OpenAIResponsesProtocolClient (internal shared core)
    ├─ PublicOpenAIResponsesProfile
    │    └─ OpenAIResponsesClient
    └─ ChatGptCodexResponsesProfile
         └─ OpenAICodexResponsesClient
```

profile 负责：

- `ApiSpecId`；
- relative request URI；
- reasoning effort wire mapping；
- body field allowlist/fixed fields；
- tool-choice capability；
- 逐请求的 header/auth configuration；
- provider-specific non-2xx classification。

canonical BaseAddress、relative request URI 与 live transport construction 都由同一个 internal
`ChatGptCodexResponsesProfile` 定义。factory 不得复制一份 endpoint 常量；它必须在读取 credential 或构造 client 前，
将 resolved `CompletionConnectionConfig.BaseAddress` 与该 profile 的 canonical BaseAddress 做 ordinal exact comparison。
`baseAddressEnv` 只要 resolve 后得到同一 canonical value即可接受。

shared core 继续负责：

- `CompletionRequest` 投影；
- JSON serialization；
- SSE framing/parsing；
- aggregation/observer/cancellation；
- terminal evidence 与 incomplete cleanup。

## 4. 建议的公共类型

名称在实施前可以做一次 API 命名复核；本文以下名称作为默认推荐。

```csharp
public interface ICodexSubscriptionCredentialProvider {
    ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default
    );
}

// 必须是普通 sealed class，不使用会自动展开字段的 record。
[DebuggerDisplay("{ToString(),nq}")]
public sealed class CodexSubscriptionCredential {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _accessToken;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _accountId;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _residency;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string AccessToken => _accessToken;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string AccountId => _accountId;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? Residency => _residency;
    public string AccountFingerprint { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public long Generation { get; }

    // 供外部 credential provider 构造 opaque snapshot；没有 public secret getter。
    public static CodexSubscriptionCredential Create(
        string accessToken,
        string accountId,
        string? residency,
        DateTimeOffset? expiresAt,
        long stableGeneration
    );

    public override string ToString() => nameof(CodexSubscriptionCredential);
}

public sealed class OpenAICodexResponsesClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; }
        = CompletionReasoningEffort.ProviderDefault;

    public int MaxConcurrentRequests { get; init; } = 3;

    // Construction-time operational identity. The default is suitable for a
    // generic Atelia host; Galatea composition defaults to "galatea".
    public string Originator { get; init; } = "atelia";
    public string ProductName { get; init; } = "Atelia";
    public string? ProductVersion { get; init; }

    // Host provisioned, non-secret, domain-separated fingerprint of the
    // one expected ChatGPT account. It is not a token hash.
    public required string ExpectedAccountFingerprint { get; init; }
}

public sealed class OpenAICodexResponsesClient : ICompletionClient,
    IDisposable {
    public OpenAICodexResponsesClient(
        ICodexSubscriptionCredentialProvider credentialProvider,
        OpenAICodexResponsesClientOptions options
    );

    public string Name => "chatgpt.com";
    public string ApiSpecId => "openai-codex-responses-v2";
}
```

public constructor 由 client 创建并拥有安全的 live transport。scripted `HttpMessageHandler`、非 production endpoint
和 `TimeProvider` 只通过 internal constructor 注入，并继续使用现有 `InternalsVisibleTo` 测试边界；第一阶段不提供
public arbitrary handler/host escape hatch。

opaque credential type 不提供 public token/account getters，private secret fields 标记为 debugger hidden，默认
System.Text.Json 也看不到可序列化的 secret property。`Create(...)` 只用于 credential provider 把 secret 交给同 assembly
内的 client；任何 exception、validation 或 source-generated serializer 都不得回显其参数。

新 client 必须实现 `ICompletionClient` 的两个 `StreamCompletionAsync` overload。first slice 与当前
`OpenAIResponsesClient` 保持一致：所有合法 `PromptCacheReuseHint` 都显式接受为 validated no-op；不能依赖 interface default
导致非-default hint 意外 fail。`TimeProvider` 注入 credential provider 的 internal test constructor，而不只注入 client。

当前 Codex body profile 没有经验证的 provider-neutral `CompletionRequest.MaxTokens` mapping。first slice 对非 null
`MaxTokens` 在 credential/file/network side effect 前 fail fast，不能像现有 public Responses path 一样静默忽略；只有在
pinned wire evidence与 tests 锁定字段语义后才开放映射。

`Originator` 是构造期参数而不是 protocol constant；它有 `atelia` 默认值，并按
`^[a-z][a-z0-9._-]{0,63}$` 校验。Galatea composition 默认传：

```text
originator: galatea
User-Agent: Atelia.Galatea/<真实版本> (<os>; <arch>)
```

operator 可通过 `ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR` 覆盖 Galatea 默认值。其他 Atelia host 直接使用该 client 时，
传该 host 的真实 identity，例如 `atelia`，不要硬编码 `codex_cli_rs`、
`pi`、`opencode` 或其他产品的 fingerprint。client identity 是 operational telemetry，不进入 durable dispatch
identity。

## 5. Borrowed credential MVP

### 5.1 支持契约

新增 `CodexCliAuthFileCredentialProvider`：

- 默认只在 construction 时解析一次 `$CODEX_HOME`，缺省为用户 home 下的 `.codex`；不使用 process CWD fallback；
- 非空 `$CODEX_HOME` 必须已经是 absolute path；relative value 直接拒绝，不能用 `Path.GetFullPath` 偷偷引入 CWD；
- 允许显式绝对路径 constructor，供 host composition 与离线测试使用；
- 每个 logical attempt 重新打开并读取一个 coherent snapshot；
- 只接受 `auth_mode == "chatgpt"`；
- 只把 `access_token`、`account_id`、可选 expiry/residency 投影到运行时 credential；
- 不把 `refresh_token` 或 `id_token` 放进返回对象；
- 不调用 OAuth authority，不修改、复制或删除 Codex credential；
- 每次 snapshot 都与 Host provisioned `ExpectedAccountFingerprint` exact 对照，从而跨进程重启 fail closed；
- account 在同一个 client lifetime 内发生变化时同样 fail closed；
- provider 切到 keyring/auto 且 file 消失时，返回 typed `AuthStorageUnavailable`，不尝试读取私有 keyring 格式。

这条路径要求有且仅有一个 Codex owner 负责刷新该 auth store。Atelia 可以与该 owner 同时只读/调用，但不能成为第二个
refresh writer。若长期不运行 Codex，access token 最终会过期，Atelia 应要求 operator refresh/relogin。

`connectionId` 是 operator 对“同一 subscription account credential lineage”的稳定承诺。同一 account 的 token rotation
保留 connection id；切换到另一个 ChatGPT account 必须使用新的 connection id、重新 provision
`ExpectedAccountFingerprint`，并先处置不能在新 account 下恢复的旧 Prepared/Started work。该 fingerprint 是
domain-separated account-id fingerprint，不是 token hash，不进入 SessionJournal durable identity；它由 Host composition
在 connections V1 之外提供。未来若需要多 account 机器路由，再通过 strict connections V2 引入非 secret
`credentialId`，不能把 raw account id 加进 V1。

### 5.2 文件安全

Linux first slice 使用 handle-based、component-safe no-follow、bounded reader，不复用普通 `File.ReadAllText`：

- final file 必须是当前用户拥有的 regular file；
- 拒绝 final symlink/reparse、execute bits，以及 group/other 的任何权限；接受 `0400` 或 `0600`；
- credential directory 必须由当前用户拥有，至少拒绝 group/other write；推荐 `0700`；
- JSON 文件上限 128 KiB，单个 token 上限 64 KiB，strict UTF-8，depth bounded；
- critical fields 的 duplicate/case-variant duplicate 必须拒绝；未知字段允许忽略以容纳 Codex schema 演进；
- 先打开并验证受检 credential directory handle，再通过 anchored `openat`/`openat2` 打开 final file；ancestor/final
  component 都拒绝 symlink，不能用“先 lexical 检查、后按完整 path open”冒充 TOCTOU-safe；
- 同一个 final handle 完成 stat/read/parse；检测到 truncate/partial write 时最多重新打开一次，仍失败则返回 typed temporary
  unreadable error；
- 不在异常中附带 raw JSON、token prefix/hash、raw account id 或完整 credential path；
- 解析后的原始 byte buffer 在 `finally` 清零。

本次本地验收观察（不是通用设计 authority）：2026-08-25 的当前机器上，`/root/.codex/auth.json` mode 为 `0755`；
父目录 `/root` 为 `0700`，所以它当前并非全机可读，但文件自身仍不满足上述最小权限契约。后续 live implementation
acceptance 前应由 operator 明确收紧为 `0600`。本设计阶段不修改该文件或权限。

Windows/keyring 支持不进入 first slice；对应平台必须明确 `PlatformNotSupported` 或 `AuthStorageUnavailable`，不能静默
退化为宽松读取。

### 5.3 expiry 与 generation

JWT decode 只用于提取未验证的 routing/expiry metadata，不冒充本地身份验证。真正的 bearer/account 验证仍由 backend
完成。

provider 在进程内为 effective token/account snapshot 分配一个不含 secret 的 generation number：相同 access token、
account id 与 expiry/residency 重读时必须复用同一 generation，只有 effective snapshot 改变才递增。generation 只用于比较
“401 之后文件是否已被 Codex owner 更新”，不持久化、不记录 token hash；绝不能按每次读取机械递增。

account fingerprint 使用 code-owned domain separator 与 exact account id 计算稳定 SHA-256，例如
`sha256("atelia-chatgpt-account-v1\0" || utf8(accountId))`。后续应提供一个本地 provisioning command：安全读取一次
credential，只输出 fingerprint，不输出 raw account id/token，并由 operator 把该值放入 Galatea Host composition 的
启动期本地设置。它不是 credential、不能用于认证，也不进入 Completion connection/SessionJournal fingerprint。

- 明确过期：在 inference network 前返回 `AuthOwnerRefreshRequired`；
- 无法取得 expiry：允许发出，由 backend 决定；
- account id 缺失或与 access-token claim 明确冲突：fail closed；
- file logout/delete：立即失败，不继续使用无限期 last-known-good token。

## 6. Codex wire profile

### 6.1 固定 transport

production endpoint 固定为：

```text
POST https://chatgpt.com/backend-api/codex/responses
```

安全 transport 至少设置：

- exact `https` scheme、host `chatgpt.com`、default 443，无 userinfo/query/fragment；
- `AllowAutoRedirect = false`，所有 3xx 都是 terminal failure；
- cookies disabled；
- default credentials disabled；
- 保留平台 TLS trust 与正常 proxy 支持，不做 certificate pinning；
- SSE 调用仍使用 `ResponseHeadersRead` 与无限 `HttpClient.Timeout`，生命周期只由 caller cancellation 或具体 transport
  failure 决定；
- 当前 2026-08-25 live backend 的成功 SSE 响应可能省略 `Content-Type`。Codex profile 只接受 exact
  `text/event-stream` 或 header 缺失；缺失时仍必须通过完整 SSE framing、JSON event 与 semantic terminal 校验。
  显式 `application/json`、`text/html` 或其他 media type 均 fail closed。public Responses client 不使用这个兼容口。

### 6.2 每次请求 headers

从同一个 immutable credential snapshot 设置：

```http
Authorization: Bearer <access-token>
ChatGPT-Account-ID: <account-id>
originator: <honest stable host identity>
User-Agent: <real product/version/platform>
Accept: text/event-stream
Content-Type: application/json
```

若 access-token 中存在已识别的 compute residency metadata，可按 pinned adapter version 发送
`x-openai-internal-codex-residency`。它是未文档化 routing hint；格式漂移必须形成 adapter compatibility failure。

首版不发送 `session-id`、`thread-id`、`x-client-request-id` 或 `prompt_cache_key`。这些不是已证明的认证必需字段；若以后
确有 session affinity/cache 收益，应先在 runtime-only `CompletionInvocationOptions` 中设计，不写入
`CompletionRequest` 或 SessionJournal durable events。

### 6.3 request body

Codex profile 固定：

```json
{
  "store": false,
  "stream": true,
  "include": ["reasoning.encrypted_content"]
}
```

并复用当前 Responses 的：

- `model`、`instructions`、`input`；
- function tools；
- `tool_choice`、`parallel_tool_calls`；
- reasoning config；
- encrypted reasoning item replay。

Responses function tool 的 provider projection 采用以下 code-owned 规则：

- function name 必须匹配 ASCII `[A-Za-z0-9_-]{1,64}`；像
  `recap_grid.control` 这样的 dotted name 在任何 credential/network side effect 前拒绝。RecapGrid Agent Control
  的 canonical name 已 hard-cut 为 `recap_grid_control`，不保留 alias；
- 只有整棵 `ToolSchema` 都满足 strict compatibility 才发送 `strict:true`：每个 object 必须非空、
  `additionalProperties:false`，且它的每个 property 都是 required；object/array 内的嵌套节点递归使用同一规则；
- 任一 optional property、empty object 或 `additionalProperties:true` 会使该 tool 发送 `strict:false`。原始 JSON Schema
  与运行时 exact validation 保持不变，不能为了迎合 strict wire 而把 optional 字段伪装成 required/null。

这套投影由 public Responses 与 Codex sibling 共用。2026-08-26 的真实 `gpt-5.6-sol` direct-backend matrix 证明：
无 tool 成功；underscore required tool（包括 string constraints）成功；dotted required tool 返回 HTTP 400；underscore
optional tool 在旧的无条件 `strict:true` 投影下返回 HTTP 400。因此这不是 reasoning Max、constraints 或
Responses Lite profile 导致的本次故障。投影变化同时把 public/Codex `ApiSpecId` 分别 bump 为
`openai-responses-v2` / `openai-codex-responses-v2`，使旧 frozen request 不会绑定到新的 request adapter。

Codex options 不暴露 `Store`、`IncludeEncryptedReasoning` 或 arbitrary `ExtraBody`。新增 body field 必须进入
Codex-specific allowlist、tests 和 request-adapter fingerprint review。

首版支持当前 provider-neutral `ProviderDefault`、`Auto`、`None` 与 `RequiredAny` 投影。由于尚无 pinned
source fixture 或 opt-in live acceptance 证明 private backend 的 named-choice shape，`RequiredNamed` 在
credential/file/network side effect 前 fail closed，不会静默降级为 `auto`。

model id 保持显式配置并原样发送，不在 Atelia 硬编码临时 model allowlist。entitlement/catalog mismatch 由 typed provider
failure 暴露；public OpenAI model catalog 不能作为 subscription backend 的 authority。

### 6.4 独立 protocol identity

推荐固定：

```text
connection kind:             openai-codex-responses
completionSurfaceId:         openai-codex-responses
client ApiSpecId:            openai-codex-responses-v2
reasoning mapping id:        openai-codex-responses-effort-v1
```

虽然两条 route 当前共享大部分 Responses JSON/SSE shape，但 direct backend 是独立、未文档化 surface。独立
`ApiSpecId` 可防止 public `OpenAIResponsesReasoningBlock` 被误认为可在 Codex route replay，反向亦然。

shared converter 应由 profile 传入 expected ApiSpecId 与 reasoning mapping，而不是继续把
`openai-responses-v2` 写死。reasoning replay 仍必须满足完整 `Origin == targetInvocation`。

初始 effort mapping 可以由当前 pinned Codex source校准后实现；即使 wire 值与 public Responses 当前相同，也使用独立
mapping id，避免未来一边变化时 silent drift。

identity 仍保持单一 owner：`CompletionDispatchIdentityFactory.ResolveReasoningMappingId(connection)` 新增 exact kind case，
返回 `openai-codex-responses-effort-v1`；profile 只实现 wire mapping，并以成对测试锁住二者一致，不引入第二套 runtime
identity source。`openai-codex-responses-v2` 代表整套 request/replay/SSE adapter contract：route、body policy、headers、
provider-native replay 或 accepted terminal 的语义变化需要 bump `ApiSpecId`；纯 effort mapping 变化只 bump mapping id。

## 7. 错误、重读与重试

稳定错误以 typed reason/code 分支，不解析人类 message：

| 条件 | 行为 |
|---|---|
| file missing/keyring-only/logout | `AuthStorageUnavailable` |
| partial write/暂时不可读 | bounded reopen 一次；仍失败为 `AuthSnapshotTemporarilyUnreadable` |
| 非 `chatgpt` auth mode | `UnsupportedAuthMode` |
| unsafe mode/owner/symlink | `CredentialStorageUnsafe` |
| token expired | `AuthOwnerRefreshRequired`，network 前失败 |
| account mid-process changed | `AuthAccountChanged`，禁止自动切换 |
| current declaration / historical tool call 的 function name 不满足 Responses profile | converter 在 credential/network 前抛 typed local no-dispatch rejection；只分类这一 exact validator |
| HTTP 401 | singleflight 重新读取一次；仅当 generation 已变化时，以 byte-identical body 最多重试一次；unchanged generation 或第二个 401 是 typed pre-stream known rejection |
| HTTP 403 | typed pre-stream known rejection，不重试，也不声称“封号” |
| HTTP 429 | typed pre-stream known rejection；durable payload 只保留 adapter-owned status/reason，不复制 `Retry-After` 或 provider metadata；不立即重试、不换账号 |
| HTTP 400 | 当前 private backend 的 live envelope 只有 free-form `detail`；不解析、不升级为 known rejection |
| HTTP 3xx | `UnexpectedBackendRedirect`，不 follow |
| 408/409/其它未验证 4xx、transport/5xx、2xx non-SSE、SSE malformed/EOF/terminal 前断流 | 沿用 outcome-uncertain/recovery 边界，不透明重试 |

first slice 把“HTTP 401 且尚未收到任何 SSE payload”视为当前 pinned adapter 下的 pre-stream authentication rejection，
因此仅在 credential generation 确实变化后允许一次 byte-identical retry。这是未文档化 backend 的受控假设，不是公开
协议证明；任何已收到 SSE payload 的调用都绝不重试，第二个 401 也立即 terminal。

Responses function-name validator 在 protocol core 请求 credential 或执行 HTTP callback 前运行；current tool declaration 与
historical `ActionBlock.ToolCall` 的 dotted、超长或其它 profile-invalid name 会抛 provider-neutral
`CompletionRequestRejectedException`，携带 code-owned `openai.responses.invalid-function-name` 与
`adapter-validation=function-name`，不复制 rejected name。这个 typed local rejection 证明 request 未 dispatch、observer 零
delta；其它 converter、serialization 或 replay exception 不得被泛化 catch，仍保持 Started uncertain。

同一 exception 也用于 remote known rejection：exhausted 401、403 与 429 在 request callback 尚未把 response 交给 SSE
parser、observer 零 delta 的位置，翻译为 `CompletionRequestRejectedException`。它只携带
`CompletionTerminationKind.Failed`、稳定 provider reason
以及 adapter-owned HTTP status，不保留 `InnerException`。即使 provider 的 `code/type/param/request-id` 是 bounded printable
ASCII，也可能包含 secret；字符集约束不是 taint sanitizer，因此这些字段与 `Retry-After` 均不得进入 durable rejection。
SessionJournal 可以据此把现有 Started
attempt 精确提交为既有 `CompletionAttemptFailed`；若 Failed append 自身抛错，当前 engine 进入 reopen-required，重开后
再由物理 HEAD 裁决为 Started 或 exact Failed。该翻译没有改变 Codex request/replay/SSE adapter wire，也没有新增
SessionJournal event/body schema，因此不额外 bump `ApiSpecId`。

2026-08-26 的真实 invalid-model probe 表明 HTTP 400 body root exact keys 只有 `detail`，没有 `error.code/type/param`。
`detail` 是 provider free-form message，既不能穿过 redaction boundary，也不足以构成稳定 allowlist，所以当前所有 400
继续抛 adapter exception，由 SessionJournal fail closed 为 Started uncertain。未来只有新的 live 校准同时给出严格、安全、
稳定的 machine envelope，并由离线 tests 锁住 exact tuple 后，才可窄化某一类 400；不得把“全部 400”视为 known rejection。

ordinary non-2xx exception 不附 raw response body或 header dump。client 最多读取 16 KiB 的 strict UTF-8 JSON；
经过字符/长度约束的 `code/type/param/request-id` 只作为显式 opt-in 的 opaque operational properties 暂存，仍视为
provider-controlled、可能敏感，禁止写入 `Message` / `ToString()`、durable journal 或普通日志。raw `message`、未知字段、
超限/非法 UTF-8/过深 JSON 与不安全 token 全部丢弃。现有 generic `CompletionHttpRequestUtility` 会把截断 response body
写入 exception，Codex profile 不得直接复用这条错误文本路径。

同一 redaction boundary 也必须覆盖 HTTP 200 SSE 内的 `error`、`response.failed` 与 nested provider message。现有
`OpenAIResponsesStreamParser` 会把部分 provider message 放入 `CompletionResult.Errors`，随后可能由
`LoggingCompletionClient` 落盘；Codex profile 必须在交给 aggregator 前只投影 allowlisted error code/type/request-id，
禁止 raw `message`、raw event JSON 与 account/token canary 进入 result、observer 或日志。shared public Responses parser 的
现有行为保持不变，Codex-specific sanitization 需要独立 contract test。

## 8. 并发与 Host composition

`OpenAICodexResponsesClient` 以全局 `SemaphoreSlim` 控制整个 client 的在途请求：

- 默认 `3`；
- code-owned accepted range 为 `1..8`；
- 等待 gate 时尊重 caller cancellation；
- slot 覆盖完整 HTTP/SSE lifetime；
- 429 不触发内部 retry storm。

这不是 OpenAI 公布的“安全阈值”，只是本项目针对单人本地订阅的保守 admission policy。

Host composition 使用 factory decorator，不修改 `ICompletionClient`：

```text
CodexSubscriptionCompletionClientFactory
    ├─ exact intercept kind=openai-codex-responses
    └─ delegate all other kinds to DefaultCompletionClientFactory
```

first slice 的 `connections.json` 不新增字段：

```json
{
  "v": 1,
  "connections": [
    {
      "id": "chatgpt-codex",
      "kind": "openai-codex-responses",
      "modelId": "<operator-selected-codex-model>",
      "completionSurfaceId": "openai-codex-responses",
      "baseAddress": "https://chatgpt.com/backend-api/codex/",
      "reasoningEffort": "provider-default"
    }
  ],
  "defaultConnectionId": "chatgpt-codex"
}
```

V1 仍要求 `baseAddress`，所以这里把它当作 operator-readable assertion；Codex factory 必须 exact 验证该值，不能把它
当任意 bearer destination。canonical BaseAddress 与请求 relative URI 必须来自 §3.3 的同一个 internal profile；factory
在任何 credential/file/network side effect 前校验 resolved value，client 从同一 profile 构造 transport，禁止三处复制
常量。`baseAddressEnv` resolve 后 exact 相等可以接受；`apiKey`/`apiKeyEnv` 对这个 kind 必须禁止。

Galatea 启用该 kind 时还必须满足 deployment precondition：

- listen URLs 全部为 loopback；
- Galatea config 恰好只有一个 configured user；
- exact 一个 Codex subscription connection/credential owner；
- 不把 connection 暴露给其他本地用户、LAN 或公网。

当前 Galatea bootstrap template 的 `0.0.0.0` 与 `alice`/`bob` 不满足这些条件；后续 Host integration 应在 startup
fail closed，而不是只写 warning。该约束属于 Galatea composition，不应污染 provider-neutral
`Completion.Abstractions`。

当前 Galatea connection catalog 是 host-global，没有 per-user connection ACL。first slice 因而不能声称“多个 Galatea
user 中只有一个能选择 subscription connection”；只要存在该 kind，就必须以“恰好一个 configured user”实现可执行的
安全边界，并在任何 client/credential provider side effect 前完成 startup validation。以后若要多个 Galatea user 但只
授权其中一个，需要 Galatea root config V2 或独立 ACL policy，不是 connections V2。其他本地 OS 用户无法访问、登录
密码只由本人掌握等仍是 operator precondition，startup 不冒充能证明这些外部事实。

## 9. Logging 与 secret hygiene

- `CodexSubscriptionCredential` 没有 public secret getter，禁止默认 record `ToString()`；private secret fields 使用
  `DebuggerBrowsable(Never)`，serialization/structured-log/debugger canary tests 必须证明不会展开 token/account。
- `Authorization`、access/refresh/id token、raw account id、auth path 不进入 `DebugUtil`、Completion call log、golden
  log、exception 或 HTTP API response。
- HTTP raw exchange capture 不记录 request headers，这是可复用的安全性质；但它会完整记录 request body 中的 system
  prompt、history、tool schema 与已消费的 provider response，因此不是普通应用日志。它只能由显式 diagnostic harness
  选择一个新的 absolute ephemeral path 来启用；Unix sink 以 `0600` 创建文件，并拒绝追加到非 `0600` 或 symlink 的
  既有路径。诊断结束后 operator 必须删除该文件；不得把 raw capture 挂到 Galatea production composition。
- future OAuth exchange/refresh 必须使用独立 auth transport，永远不挂 Completion golden capture，因为 refresh token
  会出现在 request body。
- call log 可以记录固定 `credentialSource=codex-auth-file`、client kind 和非 secret generation；不记录 token
  prefix/hash。

## 10. 实施工作包

### WP-0：行为保持型 Responses profile seam（已实施）

目标：抽 internal shared core/profile，保持现有 `OpenAIResponsesClient` 的 public API、URI、body、parser、tests 与
fingerprint 不变。

完成标准：

- 现有 Completion.Tests 全部通过；
- public client 仍只发 `v1/responses` 与 static API-key Bearer；
- 没有 Codex header/path 泄漏到 public client。

### WP-1：只读 credential provider（已实施）

目标：实现 `ICodexSubscriptionCredentialProvider`、safe auth-file reader、typed errors 与 redaction。

完成标准：

- 单次读取只产生 coherent token/account snapshot；
- effective credential 未变时 generation 稳定，token/account 变化时递增；
- Host provisioned account fingerprint mismatch 跨重启 fail closed；
- 不 materialize refresh token；
- mode/owner/symlink/bounds/duplicate/partial-write/expiry tests 通过；
- credential JSON/structured-log/debugger canary 不可见；
- 测试只使用显式 temp fixture，不探测真实 home。

### WP-2：Codex SSE client（已实施）

目标：实现固定 endpoint/header/body profile、client identity、并发 gate 与 status classification。

完成标准：

- zero real network 的 scripted handler tests 覆盖完整 request 与 SSE terminal；
- `store=true`/arbitrary ExtraBody 不存在于 public surface；
- 两个 `ICompletionClient` overload 都显式实现；合法 prompt-cache hints 为 validated no-op；non-null `MaxTokens` fail fast；
- 401 generation-change retry 至多一次；403/429/3xx/5xx 无透明 retry；
- non-2xx 与 SSE terminal error 都经过 Codex-specific sanitizer；
- public/Codex reasoning payload cross-replay fail fast。

### WP-3：factory、dispatch identity 与 Galatea composition（已实施）

目标：intercept 新 kind，锁定 fingerprint，并满足 loopback/one-user/one-connection startup precondition。

完成标准：

- strict connections V1 未扩字段；
- existing known kinds 的 fingerprint byte-identical；
- token/account/path rotation 不改变 durable fingerprint；
- kind/surface/base/ApiSpec/mapping 变化会改变相应 identity；
- frozen fingerprint 与 `BindExact` mismatch tests 锁定 adapter version；
- Codex factory 在 credential/file/network side effect 前完成 kind、surface、canonical endpoint 与 forbidden key validation；
- two-user Galatea + Codex connection 在任何 client/credential provider call 前 startup fail closed；
- registry 复用 client，但不同 invocation 会取得新 credential snapshot；
- dispose exactly once。

### WP-4：opt-in live acceptance（已实现测试入口）

目标：只验证当前 pinned backend compatibility，不把 live test 作为普通测试依赖。

双开关：

```text
ATELIA_RUN_CODEX_SUBSCRIPTION_LIVE=1
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=<explicit operator-selected real Codex auth file>
```

tool-shape acceptance 使用独立开关，普通测试套件同样为零调用：

```text
ATELIA_RUN_CODEX_SUBSCRIPTION_AGENT_CONTROL_LIVE=1
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=<explicit operator-selected real Codex auth file>
ATELIA_CODEX_SUBSCRIPTION_LIVE_MODEL=gpt-5.6-sol
```

规则：

- 不回退真实默认 auth path；
- 只读 operator 明确指定的真实 file-backed Codex auth file；不复制整份 `auth.json`，尤其不复制 refresh token；
- 默认不启用 raw/call log；
- 只发送一次小型请求并要求 semantic terminal；
- 不故意制造 401、429、refresh、token rotation 或封禁风险；
- live failure 只说明当前兼容性，不据此推断账号状态。

只有需要诊断 provider wire 时，tool-shape test 才接受
`ATELIA_CODEX_SUBSCRIPTION_LIVE_RAW_LOG=<fresh absolute .jsonl path>`。该文件含完整 prompt/response，Unix mode 固定
`0600`，不得复用已有路径；诊断后立即删除。此开关不属于 Galatea 或 Completion production configuration。

若以后需要 disposable fixture，必须另行定义 access-token-only live fixture schema、`0600` 创建与可靠销毁规则；不能把
真实 `auth.json` 的副本称作 disposable fixture。

### WP-5：Atelia-owned OAuth（后续独立提案）

目标：让 Galatea 拥有 browser/device login、独立 credential store、singleflight/cross-process refresh 与 crash-safe
publish。

必须另行解决：

- PKCE/device flow 与 callback ownership；
- `0700` directory、`0600` file；
- 同目录 temp、flush-to-disk、atomic replace；
- refresh-token rotation CAS 与 refresh 成功但本地 publish 失败的 recovery；
- auth transport 绝不 capture；
- 多 credential profile 时的 strict connections V2 `credentialId` 设计。

不要通过复制 Codex CLI refresh token 来 bootstrap 两个 writer。若要导入，只允许一次性 diagnostic access-token probe，
不得形成长期双 authority。

## 11. 最小离线测试矩阵

至少覆盖：

1. explicit synthetic auth fixture 产生 coherent snapshot；
2. expiry boundary 使用 injected `TimeProvider`，无 sleep；
3. missing/malformed/unsafe/expired errors 在 network 前失败且不含 secret canary；
4. public serialization、structured logging 与 debugger-display surrogate 都不能展开 credential canary；
5. file 在 invocation 间变化时下一次调用看到新 generation，在途调用保持旧 snapshot；unchanged/仅 mtime 或
   `last_refresh` 变化/token 真旋转/account 变化必须得到不同的 generation/account 结论；
6. 完整 URI 恰为 `/backend-api/codex/responses`，不含额外 `/v1`；`http`、非 443、userinfo、host suffix trap、
   absolute request URI 与 `baseAddressEnv` bypass 全部在 credential access 前拒绝；
7. exact bearer/account/originator/UA headers，refresh/id token 不进入 URI/body/header；
8. body 固定 `store:false`、`stream:true` 与 encrypted reasoning include；non-null `MaxTokens` fail fast；
9. 两个 `ICompletionClient` overload 与全部合法 `CompletionInvocationOptions` hint 行为一致；
10. 第一个 401 只在 generation 变化时重试一次，第二个 401 terminal；
11. 403/429/3xx/5xx 不重试；301/302/303/307/308 都不得产生第二次 request；
12. 8 个并发 401 reload collapse 为一次 provider reload，且全局 admission 不超过 configured cap；
13. caller cancellation 在 gate、credential read、HTTP 与 SSE 阶段保持 caller token identity；
14. synthetic Codex SSE text/tool/reasoning/terminal end-to-end；
15. metadata/unknown well-formed events 保持 forward-compatible，terminal 前 EOF/[DONE] 仍 fail closed；
16. non-2xx 与 SSE `error` / `response.failed` 中的 raw message/account/token canary 被 Codex sanitizer 移除；
    known rejection 只持久化 adapter-owned status/reason；ordinary exception 的 bounded opaque
    `code/type/param/request-id` 不进入 `Message` / `ToString()`；
17. public/Codex reasoning cross-replay 拒绝；
18. golden/call-log/exception/API response 全文扫描不包含 access/refresh/id/account canary；
19. manifest/factory/fingerprint/registry lifetime/dispose contract；account fingerprint 跨重启 mismatch；
20. Linux `0400`/`0600` 接受，`0644`/`0660`/execute/symlink/non-regular 拒绝；relative `$CODEX_HOME`、ancestor
    symlink 与 unsafe directory owner/mode 拒绝；
21. loopback classifier exact 覆盖 `127.0.0.1`、`::1`、wildcard、`0.0.0.0` 与 LAN address；
22. live smoke 只读 explicit authority file，验证没有复制或 materialize refresh token；Agent Control live acceptance
    锁定 underscore + optional schema 经 `strict:false` 的真实 backend 兼容性；
23. Responses strict capability 递归覆盖 root/nested/array optional、empty object 与
    `additionalProperties:true`；current declaration 与 historical tool call 的 dotted/超长 function name 都在
    credential/network 前形成 typed local rejection，observer/credential/network 均为零；
24. raw exchange JSONL 在 Unix 上以 `0600` 创建，拒绝非 private existing path，且 non-2xx body 被 client 消费后
    transport tee 可观察。

现有 public Responses converter/parser 的完整矩阵不应复制；新测试只覆盖 Codex profile 的差异与一个端到端复用证明。

建议实施阶段的 focused validation：

```bash
dotnet test tests/Completion.Tests/Completion.Tests.csproj --no-restore -m:1 -nr:false
```

接入 Galatea 后再串行运行对应 Galatea composition/config tests，避免并行 repository-heavy test 造成无关噪声。

## 12. 明确延期与非目标

- WebSocket、connection pool、`previous_response_id` continuation 与 SSE fallback；
- account pool、round-robin、共享、转售或公网 OpenAI-compatible façade；
- 伪装 Codex CLI/Pi/OpenCode 的 UA、originator 或 TLS fingerprint；
- generic OAuth headers provider；
- 把 auth path/token/account id 写进 SessionJournal、Prepared manifest 或 durable dispatch identity；
- 把 403 简化为“封号”；
- 在收到任何 SSE payload 后透明 retry；
- 依赖 public OpenAI model catalog 判断 ChatGPT subscription entitlement；
- 在 first slice 中读取 OS keyring 私有格式或实现 Windows credential ACL reader。

## 13. 实施阶段开始前的再验证点

direct backend 是 drift-prone implementation surface。开始 WP-2 与 opt-in live smoke 前必须重新固定并核对：

- 当前 Codex/Pi/OpenCode endpoint 与 required headers；
- current `auth.json` schema/storage mode；
- request body 的 required/fixed fields，尤其 `store`、reasoning effort 与 tool choice；
- SSE terminal/error aliases；
- subscription 可用 model ids；
- OpenAI 是否发布新的正式第三方 OAuth/harness registration 或 public backend contract。

若出现正式 public contract，应优先迁移到正式 surface，而不是继续维护内部 endpoint compatibility layer。

## 14. 当前实现记录

2026-08-25 的 Borrowed credential MVP 已落到以下主链：

- `CodexCliAuthFileCredentialProvider`：只读 file-backed Codex `auth.json`，逐路径组件
  `openat(O_NOFOLLOW)`，同一 fd 双读校验，拒绝不安全 owner/mode/symlink，永不读取成 managed string 的
  `refresh_token`/`id_token`，永不 refresh/write-back；
- `OpenAIResponsesProtocolClientCore`：public Responses 与 Codex sibling client 共用 projection、SSE reader/parser、
  aggregator，同时保持独立 `ApiSpecId` 与 reasoning mapping entry；
- `OpenAICodexResponsesClient`：固定 direct SSE route、逐 attempt credential snapshot、默认并发 3、401 仅在
  generation 改变时单次 byte-identical retry、non-2xx/transport/SSE error 的 code-owned redaction；
- `CodexSubscriptionCompletionClientFactory`：exact intercept 新 kind，验证 canonical surface/base 与 forbidden key；
- `GalateaCodexSubscriptionComposition`：只有启用新 kind 时才要求 exact one user、exact one Codex connection 和
  explicit loopback `listenUrls`，并在 server bind/client creation 前 fail closed；Codex mode 还会替换默认可 reload
  的 `Kestrel:Endpoints` loader，以 code-owned `Listen` endpoints 绑定实际 loopback，防止 appsettings、环境变量或
  reload 覆盖 `UseUrls` 安全边界。

Galatea 的 Host 环境变量为：

```text
ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT=<required sha256:...>
ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR=<optional, default galatea>
ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE=<optional absolute auth.json path>
```

未设置 auth-file override 时，provider 使用构造时解析的 `$CODEX_HOME/auth.json`，缺省为
`~/.codex/auth.json`。`ExpectedAccountFingerprint` 可由一次安全读取返回的
`CodexSubscriptionCredential.AccountFingerprint` provision；它不暴露 raw account id，也不是 credential。

首版刻意不发送 `OpenAI-Beta`、`session-id`、`thread-id`、`x-client-request-id` 或 `text.verbosity`：当前
Codex 0.147.0 证据表明它们不是 one-shot HTTP SSE 的认证/协议最低要求。file-backed provider 也不从 JWT
臆造 residency；只有自定义 credential provider 显式提供受控值时，client 才会发送 residency header。

离线验收计数是 commit-local evidence，不作为随代码自动更新的 current 总数。形成 `9860bc33` candidate 时：

- `Completion.Tests`：645 passed；
- `Galatea.Server.Tests`：189 passed；
- `git diff --check`：通过。

后续改动应按 §11 的命令重跑相关 suite，以实际命令退出码为验收 authority，而不是沿用上述历史计数。

opt-in live acceptance 位于 `OpenAICodexResponsesLiveTests`，必须同时提供对应 enable switch 与显式 absolute auth file；
它不会复制 auth file，默认不会启用 HTTP raw/call log，也不会触发 refresh/故意制造 401。Agent Control shape 使用独立
enable switch；raw JSONL 还需要显式 fresh absolute path，且只用于短期诊断。

2026-08-25 live acceptance：将 operator 选定的 `/root/.codex/auth.json` 从历史遗留 `0755` 收紧为 `0600` 后，
以 `originator=atelia`、model `gpt-5.4` 发出一次小请求，收到 semantic `response.completed`，聚合文本精确为 `OK`。
前两次诊断请求只暴露出成功响应缺失 `Content-Type` 的兼容差异，未读取/打印 response body；加入上述窄兼容和离线
回归后第三次通过。整个 acceptance 未调用 refresh endpoint、未写回 auth file 内容、未复制 credential，也未输出
token/account id。

2026-08-26 的 `gpt-5.6-sol` tool probe matrix 使用 `CompletionReasoningEffort.Max`：无 tool、required underscore、
required constrained underscore 均成功；required dotted 与 `strict:true` optional underscore 均稳定返回 HTTP 400。
据此将 Agent Control canonical name 改为 `recap_grid_control`，并把 shared Responses strict projection 改为递归 capability
判定。长期 opt-in acceptance 只保留真实 Agent Control shape（underscore + optional properties，wire
`strict:false`）；历史故障 matrix 沉淀为离线 regression 与本节事实，不进入默认测试调用面。
