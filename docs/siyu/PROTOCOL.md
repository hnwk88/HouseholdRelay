# 思域协议：HouseholdRelay 与 MyCoAI 的唯一依据

文本版本：2.0　线上协议版本：2（同时认 1）　日期：2026-11-24

这份文件是思域两边对接的唯一依据。两边以后改协议，都先改这一份，再各自改代码。

- HouseholdRelay 一侧：名字以 `siyu: protocol text` 开头的用例读这份文件里标了「契约表」的表格，与代码逐项对照，改了一边没改另一边就失败。
- MyCoAI 一侧：假中转 `FakeSiyuRelay` 的回应形状、原因码、状态码与打回照这份文件写契约用例。
- 设计历史：`artifacts/order-152/design/` 下的设计与依据只在 HouseholdRelay 工程师的本机工作区（`artifacts/` 不进仓库），留作历史，这份文件不依赖它们；与本文冲突时以本文为准。
- 「打回」草案 [REMEDY-DRAFT.md](REMEDY-DRAFT.md) 已在文本版本 2.0 并入本文（第 10 节），草案文件只留作记录。

## 变更记录

| 文本版本 | 日期 | 线上协议版本 | 改了什么 | 来源 |
| --- | --- | --- | --- | --- |
| 1.0 | 2026-11-22 | 1 | 定稿：原生文件认证、受认证 HTTPS 代理、正文原样转发，两个官方主机 | 订单 152 |
| 1.0.1 | 2026-11-23 | 1 | 加账户列表口 `GET /v1/siyu/accounts`；写明代理凭据字符集 | 提交 2fbad9c |
| 1.1 | 2026-11-24 | 1 | 客户端身份改写进用户本人的默认位置（一个目录）；中转对客户端的依赖清单；用户本人配置经过代理的口径；令牌刷新；同一身份多会话；多账户同时用的限制；「打回」草案另起文件。线上协议不变，中转代码不变 | 订单 174 |
| 2.0 | 2026-11-24 | 2（同时认 1） | 版本 2：建会话不带账户，后端按上游页「在用」那一家选官方或标准模式（第 9 节）；标准模式的建会话回应与启动（第 4.3 节）；拒绝带打回 `remedy`，可自动处理的原因码表与写死的四条（第 10 节）；新原因码 `account-switched`、`identity-mismatch`、`no-usable-upstream`、`too-many-tunnels`；续租可声明版本。顺路改三处：清理变量清单并进本文（第 4.4 节）、「中转不依赖」里资料字段的措辞（第 5 节）、隧道满的真实读句（版本 2，第 8 节）。版本 1 客户端的行为逐字不变 | 订单 180 |

## 1. 两边各管什么

- **中转**：控制面（建会话、查询、续租、停止、账户列表）、代理入口、逐请求准入与核对、把个人令牌换成官方令牌、官方续期、用量；版本 2 起还管「这一次用官方还是标准、用哪一家」（第 9 节）。中转只看线上字节，不读客户端电脑上的任何文件。
- **MyCoAI**：调控制面建会话、按回应准备文件与进程环境、起 Claude Code、续租、照打回自动处理、停止、读屏状态。
- **请求正文**：由客户端的 Claude Code 生成，中转一个字节不改（台账「中转不改请求正文」），官方模式与标准模式都一样（标准模式的例外见第 4.3 节最后一条）。
- **官方还是标准、换成哪一个**：按 2026-11-24 用户决定，只有后端知道、后端决定，客户端不判断。版本 2 落实了这一条；版本 1 仍由客户端带 `officialAccountId`，只为过渡期兼容（第 12 节）。

## 2. 线上数值

全部是固定值，唯一定义处是 `src/Core/Siyu/SiyuContract.cs` 与 `src/Infrastructure/ExternalSources/Siyu/SiyuProxyLimits.cs`，不交给用户配置。租约、限额与保留期还没在目标 Windows 上测过长时间断线和睡眠恢复，双机验收时实测，不合就改代码并在这里升文本版本。

<!-- 契约表:数值 -->
| 名字 | 值 | 含义 |
| --- | --- | --- |
| ProtocolVersions | 1、2 | 建会话的 `clientProtocolVersion` 只认这两个 |
| RequestStrategy | nativePreserve | 唯一请求策略：正文原样 |
| Transport | nativeHttpsProxy | 唯一传输：原生 HTTPS 代理（版本 2 也照填） |
| StandardProtocol | anthropic-messages | 标准模式回应里的协议名 |
| StandardAuthentication | personal-token | 标准模式回应里的认证方式 |
| RemedyWaitSeconds | 30 | 打回 `wait` 建议的等待秒数 |
| SupportedClaudeVersions | 2.1.325 | 已验证的 Claude Code 版本，其余拒建会话 |
| SupportedEntrypoints | sdk-cli | 已验证的入口，其余拒建会话 |
| LeaseDurationSeconds | 120 | 一次续租给的存活时长 |
| LeaseRenewAfterSeconds | 40 | 建议续租间隔 |
| MaxSessionsPerUser | 8 | 每个使用者同时存活的会话上限（官方与标准一起算） |
| MaxTunnelsPerSession | 16 | 每个会话同时打开的代理隧道上限 |
| CreateReplayRetentionSeconds | 600 | 同一 `requestId` 重放原结果的保留期 |
| ClosedRetentionSeconds | 600 | 会话结束后终态留给查询与停止确认的保留期 |
| McpDiscoveryLifetimeSeconds | 600 | 一次成功的连接器发现集合的有效期 |
| McpDiscoveryTimeoutSeconds | 15 | 中转补一次连接器发现的限时 |
| SweepIntervalSeconds | 5 | 后台清扫间隔 |
| ProxyHeadTimeoutSeconds | 15 | 代理入口读 CONNECT 头的限时 |
| ProxyHandshakeTimeoutSeconds | 15 | 内层 TLS 握手限时 |
| ProxyIdleTimeoutSeconds | 120 | 内层连接两次请求之间最长空闲，超了关闭，客户端自己重连 |

## 3. 控制面

只经站点可信 HTTPS（对外 8787，私有 CA 由 D-002 装好信任），前缀 `/v1/siyu`。认证只认 `Authorization: Bearer <个人令牌>`。所有回应带 `Cache-Control: no-store`。思域没打开时前缀下每个请求都回 503 `siyu-disabled`。请求 JSON 严格：多出字段、缺必带字段、类型不符都回 400 `request-malformed`。

| 方法与路径 | 请求 | 成功 |
| --- | --- | --- |
| `GET /v1/siyu/accounts` | 只要令牌 | 200 `{"accounts":[{"officialAccountId":<整数>,"displayName":"<名称>"}]}`；一个都没有是空数组。只给版本 1 用，版本 2 不需要 |
| `POST /v1/siyu/sessions` | 见 3.1 | 201，见 3.2；同一 `requestId` 同样内容重试回同一会话与票据 |
| `GET /v1/siyu/sessions/{sessionId}` | 只要令牌（同一使用者任一当前有效令牌） | 200 会话视图；不续租；已结束的会话也回 200，视图带 `closeCode` |
| `PUT /v1/siyu/sessions/{sessionId}/lease` | JSON：`leaseSequence`（从 1 起单调）；`clientProtocolVersion` 可省，省了按 1，版本 2 客户端要带 2 | 200 会话视图；同序号或更小只回现值，不重复延长；只认创建它的那一版令牌 |
| `DELETE /v1/siyu/sessions/{sessionId}` | 只要令牌 | 已结束 204；资源还没归还完 202 加会话视图（`state` 为 `closing`）；重复停止幂等 |

### 3.1 建会话请求

字段：`requestId`（1–64 个字母数字、`-`、`_`）、`expectedIdentityVersion`（首次可为 null）、`clientProtocolVersion`、`claudeVersion`、`binarySha256`（64 位小写十六进制）、`entrypoint`、`transport`，以及：

- 版本 1：必带 `officialAccountId`（从账户列表里选），缺了或为 null 回 400 `request-malformed`。
- 版本 2：**不许带** `officialAccountId`，带了回 400 `request-invalid`；后端选（第 9 节）。

### 3.2 建会话回应

版本 1 与版本 2 的官方模式：

```text
{
  "session": <会话视图>,
  "launchIdentity": { "deviceId": "<站点设备编号>", "oauthAccount": {<官方账户资料原字节>}, "credentialMetadata": {"scopes":[…], "subscriptionType":…, "rateLimitTier":…} },
  "proxy": { "endpoint": "<代理地址，不含凭据>", "authentication": "basic-session-ticket", "ticket": "<票据>", "authorityPem": "<代理 CA 公钥证书>", "authoritySha256": "<代理 CA 证书 DER 的 SHA256>" }
}
```

版本 2 的标准模式（没有票据、没有身份，第三方上游的密钥只在中转机上，永不下发）：

```text
{
  "session": <会话视图，mode 为 standard>,
  "standard": { "baseUrl": "<站点对外 HTTPS 根地址>", "protocol": "anthropic-messages", "authentication": "personal-token" }
}
```

客户端看会话视图里的 `mode` 分辨是哪一种。

### 3.3 会话视图

`serverInstanceId`、`sessionId`、`userId`、`tokenId`、`tokenVersion`、`officialAccountId`、`siteIdentityVersion`、`profileVersion`、`clientProtocolVersion`、`claudeVersion`、`entrypoint`、`requestStrategy`、`transport`、`state`（`active`、`closing`、`closed`）、`lease.expiresAt`、`lease.renewAfterSeconds`，结束了另带 `closeCode`、`closeSentence`。

- 版本 1：与文本版本 1.1 逐字相同，没有 `mode`。
- 版本 2：在 `tokenVersion` 之后多一个 `mode`（`official` 或 `standard`）；标准模式不写只属于官方模式的 `officialAccountId`、`siteIdentityVersion`、`profileVersion`。

### 3.4 拒绝回应

控制面与代理入口同一形状：

```text
{ "type": "error", "error": { "type": "siyu_refused", "code": "<原因码>", "message": "<中文读句>",
  "currentIdentityVersion": <只有版本 1 的 identity-version-stale 才有>,
  "remedy": { "action": "recreate" | "wait", "expectedIdentityVersion": <整数，有才写>, "retryAfterSeconds": <整数，只有 wait 才写> } } }
```

`remedy` 只出现在版本 2 的控制面回应里（第 10 节）。版本 2 的 `identity-version-stale` 只在 `remedy.expectedIdentityVersion` 给代次，不再另带 `currentIdentityVersion`：一个值一个来源。

### 3.5 代理凭据

`HTTPS_PROXY=https://<sessionId>:<ticket>@<endpoint 的主机与端口>`。sessionId 是 32 位小写十六进制，ticket 是 32 个随机字节去掉补位的 base64url（43 个字符），两样都只含 `[A-Za-z0-9_-]`，`Uri.EscapeDataString` 后不变；中转只做 Basic 的 base64 解码，不做百分号解码。只有官方模式有。

## 4. 客户端要准备的东西

### 4.1 官方模式：写进用户本人默认位置的三样

不再有独立配置目录。官方模式时，像切换官方账户一样，把下面三样写进用户本人的默认位置；切回用户自己的账户时还原。

| 文件 | 键 | 值从哪来 |
| --- | --- | --- |
| `%USERPROFILE%\.claude.json` | 顶层 `userID` | 建会话回应 `launchIdentity.deviceId` |
| `%USERPROFILE%\.claude.json` | 顶层 `oauthAccount` | 建会话回应 `launchIdentity.oauthAccount`，原字节 |
| `%USERPROFILE%\.claude\.credentials.json` | `claudeAiOauth` | `accessToken` 填个人令牌，`expiresAt` 为 null，不写 `refreshToken`；再加 `launchIdentity.credentialMetadata` 的三项 |

`.claude.json` 里别的键、`settings.json`、技能、挂点、插件、MCP 配置、历史都不动，它们在思域会话里也生效。官方令牌永远不下发。

### 4.2 官方模式：进程环境

| 变量 | 值 | 为什么 |
| --- | --- | --- |
| `HTTPS_PROXY` | 见第 3.5 节 | Claude Code 的推理、账户附带接口、官方连接器三条网络栈都走它 |
| `NODE_EXTRA_CA_CERTS` | 建会话回应的 `authorityPem` 存成的文件 | 只给这个进程信任代理 CA，不装系统证书库 |
| `NO_PROXY` | 见第 6.3 节 | 不能含任何官方主机 |
| `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` | `1` | 关掉遥测、错误上报、自动更新、反馈；遥测里可能带各台电脑自己的编号 |
| `CLAUDE_ENV_FILE` | MyCoAI 写的脚本 | Bash 工具每条命令前执行：去掉带票据的代理变量和代理 CA，换回用户原有的代理设置 |

不设 `CLAUDE_CONFIG_DIR`。启动前先清掉第 4.4 节的 33 项，再设上表。

### 4.3 标准模式：启动（版本 2）

后端选的是标准上游时，客户端照站点「旧接法」的标准 API 方式起进程：

| 变量 | 值 |
| --- | --- |
| `ANTHROPIC_BASE_URL` | 建会话回应 `standard.baseUrl` |
| `ANTHROPIC_AUTH_TOKEN` | 个人令牌（站点也认 `x-api-key`，两种都是个人令牌） |
| `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` | `1` |

- 启动前同样清掉第 4.4 节的 33 项；不设 `HTTPS_PROXY`、`NODE_EXTRA_CA_CERTS`、`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`。站点证书由 D-002 装好的私有 CA 信任。
- 不写第 4.1 节的三样；默认位置保持用户本人的内容（若上一轮是官方模式，先按第 4.5 节停进程、还原，再起标准进程）。
- 中转侧完全复用旧接法：转发、用量（记在使用者名下）、自动切换都照上游页的设置，与连接说明页「旧接法」同一条路，不新造通路。
- 标准模式没有设备、账户核对：正文里不再有官方身份，请求只发往独立 API 与兼容中转。前提是中转机打开了「旧接法官方请求关闭」（项目设置）：没打开时，旧接法的自动切换可能把这条请求送到官方账户，那是旧接法的既有行为（请求头换成中转机身份，正文设备段却是客户端自己的），会造成设备差异。所以**用标准模式之前，中转机必须先打开这个开关**。
- 正文：独立 API（API 密钥一类）原样转发；兼容中转（账户模式的第三方）在中转机开着「统一设备元数据」时，旧接法会把正文里的设备段改成站点设备编号（旧接法对第三方的既有行为，不涉及官方）。

### 4.4 启动前清掉的继承变量（33 项）

两种模式都在启动前清掉下面这些继承来的变量（不分大小写，大写、小写、混合写法都清），再按第 4.2 或 4.3 节设上本次的值。清单来自订单 152 对真实 `ProcessStartInfo` 的验证（大写、小写、混合三组污染，修后全部移除）：

1. 认证与供应商：`ANTHROPIC_AUTH_TOKEN`、`ANTHROPIC_API_KEY`、`ANTHROPIC_BASE_URL`、`ANTHROPIC_UNIX_SOCKET`、`CLAUDE_CODE_OAUTH_TOKEN`、`CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR`、`CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR`、`CLAUDE_CODE_OAUTH_SCOPES`、`CLAUDE_CODE_SUBSCRIPTION_TYPE`、`CLAUDE_CODE_RATE_LIMIT_TIER`、`CLAUDE_CODE_USE_BEDROCK`、`CLAUDE_CODE_USE_VERTEX`、`CLAUDE_CODE_USE_FOUNDRY`、`CLAUDE_CODE_USE_ANTHROPIC_AWS`。
2. 启动与配置目录：`CLAUDE_CONFIG_DIR`、`CLAUDE_CODE_PROCESS_WRAPPER`、`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`、`BASH_ENV`、`ENV`。
3. 客户端证书：`CLAUDE_CODE_CLIENT_CERT`、`CLAUDE_CODE_CLIENT_KEY`、`CLAUDE_CODE_CLIENT_KEY_PASSPHRASE`、`CLAUDE_CODE_CERT_STORE`。
4. Node 与 TLS：`NODE_OPTIONS`、`NODE_EXTRA_CA_CERTS`、`SSL_CERT_FILE`、`SSL_CERT_DIR`、`NODE_TLS_REJECT_UNAUTHORIZED`、`BUN_TLS_REFUSE_DISTRUSTED_KEYS`。
5. 代理：`HTTPS_PROXY`、`HTTP_PROXY`、`ALL_PROXY`、`NO_PROXY`。

152 验证共 34 项，另一项 `HR152_CAPTURE_ROOT` 是那次实验脚本自己的变量，不进产品清单。`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` 不在清单里，由启动时直接设成 `1`。

### 4.5 一个目录才有的规矩

1. **写身份文件之前，这台电脑上所有用默认位置的 Claude Code 进程都要先退出**，MyCoAI 之外在终端或编辑器里开的也算。依据：运行中的 Claude Code 按 `.credentials.json` 的修改时刻重读令牌、按 `.claude.json` 的修改时刻重读全局配置（openclaude `src/utils/auth.ts:1340`、`src/utils/config.ts:1284-1310`；openclaude 是分叉，真实 2.1.325 待两边联调实测）。没退出会出两种事：
   - 没设思域代理的旧进程读到个人令牌，直接发给官方：个人令牌落到官方手里，而且这次直连从用户本机出口出去，正文里已经是站点设备编号，等于官方看到第二台设备。中转看不到这次请求，拦不住，只能靠 MyCoAI 先停进程。
   - 切回用户自己的账户以后，还没停的思域进程读到用户本人的官方令牌，经代理发到中转。中转核对个人令牌不认，本地回 401，不转发给官方，也不记令牌的值；但用户本人的令牌已经到了中转机。
2. **同一台电脑同一时刻，所有官方模式的思域会话必须绑同一个官方账户**：身份文件只有一份。版本 2 由后端选，同一时刻建的会话本来就绑同一家；后端换了，旧会话会以 `account-switched` 结束（第 9 节），MyCoAI 停掉全部旧进程再写新身份。
3. **切回用户自己的账户、或从官方模式换到标准模式**：先停全部思域会话（`DELETE`），确认进程退出，再还原三样。
4. **启动前查用户本人设置里的冲突项**，有就不启动，读出是哪一项、在哪个文件、怎么改；不替用户改设置：
   - `%USERPROFILE%\.claude\settings.json` 的 `env` 里出现下面任一个（不分大小写）：`HTTPS_PROXY`、`HTTP_PROXY`、`ALL_PROXY`、`NO_PROXY`、`NODE_EXTRA_CA_CERTS`、`NODE_TLS_REJECT_UNAUTHORIZED`、`ANTHROPIC_BASE_URL`、`ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`CLAUDE_CODE_OAUTH_TOKEN`、`CLAUDE_CODE_USE_BEDROCK`、`CLAUDE_CODE_USE_VERTEX`、`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`。依据：设置里的 `env` 在进程启动后整个盖到进程环境上（openclaude `src/utils/managedEnv.ts:142-155`），会把 MyCoAI 设的代理、证书、认证改掉。
   - 同一个设置文件里有 `apiKeyHelper`，或 `forceLoginMethod` 不是 `claudeai`（只对官方模式）。依据：有 apiKeyHelper、ANTHROPIC_AUTH_TOKEN、ANTHROPIC_API_KEY 时客户端不走官方登录态（openclaude `src/utils/auth.ts:105-142`），正文账户段变成空串（`src/services/api/claude.ts:568`），中转回 403 `account-mismatch`；带 `x-api-key` 的请求中转回 400 `authorization`。
   - 真实 2.1.325 的覆盖顺序待两边联调实测；实测前按会覆盖处理。

## 5. 中转对客户端的全部依赖（官方模式）

中转只靠下面这些线上事实判断，每一项都写了中转在哪里用、缺了会怎样。它们在客户端从哪来，写在「客户端来源」一列。标准模式只依赖个人令牌（旧接法的鉴权），不在这张表里。

<!-- 契约表:依赖 -->
| 序号 | 依赖 | 客户端来源 | 中转在哪里用 | 缺了或不对会怎样 |
| --- | --- | --- | --- | --- |
| 1 | CONNECT 带唯一 `Proxy-Authorization: Basic 会话编号:票据` | 环境变量 `HTTPS_PROXY` 里的凭据 | `src/Infrastructure/ExternalSources/Siyu/SiyuProxyConnectionHandler.cs:67`、`:102` | 407 `proxy-authentication` |
| 2 | 外层 TLS 信任站点证书 | D-002 装进 Windows 的站点私有 CA | `src/HouseholdRelay/Siyu/SiyuProxyHost.cs:44` | 客户端握手失败，请求到不了中转 |
| 3 | CONNECT 目标与 Host 逐字等于两个官方目标之一 | Claude Code 按代理语义自己生成 | `SiyuProxyConnectionHandler.cs:64`、`:65`；`src/Core/Siyu/OfficialTargets.cs:30` | 403 `target`；400 `host` |
| 4 | 内层 TLS 信任代理 CA | 环境变量 `NODE_EXTRA_CA_CERTS` | `SiyuProxyConnectionHandler.cs:85` | 客户端握手失败，失败关闭 |
| 5 | 内层 SNI 等于这条隧道的官方主机，ALPN 只协商 http/1.1 | Claude Code 自己生成 | `SiyuProxyConnectionHandler.cs:94`、`:95` | 连接直接关闭 |
| 6 | 内层 Host 等于隧道主机，方法与路径在路由白名单里 | Claude Code 自己生成 | `src/Infrastructure/ExternalSources/Siyu/SiyuInnerExchange.cs:61`、`:62`；`src/Core/Siyu/SiyuRoutes.cs:33` | 400 `host`；403 `route` |
| 7 | HTTP/1.1，请求体只用单个 Content-Length，不带 Transfer-Encoding、Expect、Upgrade | Claude Code 自己生成 | `src/Infrastructure/ExternalSources/Siyu/Http1RequestHead.cs:43-50` | 第 15 节格式码表里的状态码 |
| 8 | 恰好一个 `Authorization: Bearer <个人令牌>`，不带 `x-api-key` | `.credentials.json` 的 `claudeAiOauth.accessToken`；用户设置不能让客户端改走 API 密钥（第 4.5 节第 4 条） | `Http1RequestHead.cs:61-70` | 400 `authorization` |
| 9 | 这个个人令牌就是建会话的那一版 | 同上 | `src/Application/Services/Siyu/SiyuSessionService.cs:359-360`、`:496-500` | 401 `personal-token-invalid` |
| 10 | Messages 正文 `metadata.user_id` 的设备段等于站点设备编号 | `.claude.json` 顶层 `userID` | `SiyuSessionService.cs:474-488`；`src/Core/Relay/DeviceMetadataRewrite.cs:51-75` | 403 `device-mismatch`；版本 2 另在下一次续租以 `identity-mismatch` 结束 |
| 11 | 同一字段的账户段等于绑定账户的 `accountUuid` | `.claude.json` 顶层 `oauthAccount.accountUuid`，且客户端处于官方登录态（`scopes` 含 `user:inference`） | 同上 | 403 `account-mismatch`；版本 2 同上 |
| 12 | `metadata.user_id` 是两种已支持格式之一，唯一、不重复 | Claude Code 自己生成 | 同上 | 403 `identity-unsupported`；版本 2 同上 |
| 13 | 官方连接器编号在同账户当次成功发现的集合里 | 客户端先经思域发 `GET /v1/mcp_servers`（客户端按 `scopes` 决定发不发） | `SiyuSessionService.cs:362-375` | 403 `mcp-not-discovered`、`mcp-connector-unknown` |
| 14 | 建会话的协议版本、传输、Claude Code 版本与入口、二进制摘要、身份代次 | MyCoAI 填，不来自配置目录 | `SiyuSessionService.cs:122-123`、`:738-750` | 426 `protocol-unsupported`、`version-unsupported`；400 `request-invalid`；409 `identity-version-stale` |

**中转不读的东西**：`settings.json` 的任何键、`CLAUDE_CONFIG_DIR`、`CLAUDE_ENV_FILE`、`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`、`.claude.json` 里 `userID` 与 `oauthAccount` 以外的键、凭据文件的 `expiresAt` 与有无 `refreshToken`，产品代码里都不读（`ClientLauncher.cs:67`、`IsolatedClaudeEnvironment.cs:23` 两处是中转机自己的 claude.exe，与客户端无关）。`subscriptionType` 与 `rateLimitTier` 在产品代码里有，但方向相反：那是中转从自己机器上的官方账户读出、在建会话回应的 `credentialMetadata` 里下发给客户端的资料字段（`src/Core/Siyu/SiyuLaunchProfileContract.cs:48-49`），中转不读客户端写回的值。这些东西影响的是客户端发不发、发成什么样，间接落到上表某一项上：`scopes` 决定第 11、13 项，`expiresAt:null` 与没有 `refreshToken` 决定客户端不刷新（第 7 节），非必要流量开关决定遥测发不发（第 6.2 节）。

**结论**：MyCoAI 把同样三样写进用户本人默认位置、进程环境照旧，中转原样放行。中转眼里独立目录与一个目录的客户端没有区别：它只看线上字节，而两种客户端发出的字节一样。差的只在客户端一侧：用户本人设置里的冲突项（第 4.5 节第 4 条）和切换时没退出的进程（第 4.5 节第 1 条），这两件由 MyCoAI 在启动前挡住。

## 6. 经过代理的流量（官方模式）

### 6.1 隧道白名单与路由

<!-- 契约表:主机 -->
| 主机 | 端口 |
| --- | --- |
| api.anthropic.com | 443 |
| mcp-proxy.anthropic.com | 443 |

<!-- 契约表:路由 -->
| 主机 | 方法 | 路径 | 用途 |
| --- | --- | --- | --- |
| api.anthropic.com | POST | /v1/messages | Messages |
| api.anthropic.com | POST | /v1/messages/count_tokens | CountTokens |
| api.anthropic.com | GET | /v1/models | Models |
| api.anthropic.com | GET | /api/claude_code/settings | Settings |
| api.anthropic.com | GET | /api/claude_code/policy_limits | PolicyLimits |
| api.anthropic.com | GET | /v1/mcp_servers | McpServers |
| mcp-proxy.anthropic.com | POST、GET、DELETE | /v1/mcp/{连接器编号} | McpConnector |

推理与估算只认空查询或 `beta=true`；模型目录、设置、限额、发现允许有界查询串（字母数字与 `_ - . = & ,`，最长 256）；连接器路径不带查询。

拒绝方式：
- CONNECT 到别的主机、别的端口、IP、子域、尾点或大小写变体：403 `target`，读句「思域只连接官方的两个地址，这个目标不在合同内。」，回完关闭连接。
- 两个主机上没登记的方法或路径：403 `route`，读句是通用句「思域拒绝了这次请求，官方没有收到它。请重新启动思域后再试。」，回完关闭这条内层连接，客户端重连即可。
- 官方回 3xx：本地 502 `redirect-refused`，不跟随、不带 Location。

### 6.2 用户本人的配置在思域会话里会多出什么

| 流量 | 谁发、走哪 | 结局 | 口径 |
| --- | --- | --- | --- |
| 推理、估算、模型目录、远程设置、限额、连接器发现、官方连接器 | claude.exe，经代理到两个官方主机 | 放行 | 不变 |
| 遥测、错误上报、自动更新、反馈 | claude.exe | 设了 `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` 就不发 | MyCoAI 必须设，且用户设置不能把它改掉（第 4.5 节第 4 条） |
| 官方家的其他主机或没登记的路径，例如令牌刷新 `platform.claude.com`、`claude.ai`、`api.anthropic.com` 上的资料刷新或网页抓取域名预检 | claude.exe，经代理 | 403 `target` 或 403 `route`，官方收不到 | 不加白名单，永不进 NO_PROXY。真实出现并影响功能时，按台账 1077 定稿的失效条件先实测再修订本文，不悄悄放宽 |
| 用户本人的远程 MCP 服务器（http、sse 类型）、插件市场（git 拉取）、网页抓取工具访问第三方网站 | claude.exe 或它直接起的 git，经代理 | CONNECT 403 `target`，这些功能失败：MCP 显示连接失败，插件装不上或更新不了，网页抓取把错误交给模型。具体表现待两边联调实测 | 见第 6.3 节 |
| Bash 工具里的命令（git、npm、dotnet、curl） | 子进程 | 走用户原有代理，照常可用：思域启动的会话里，子进程的 `HTTPS_PROXY` 是 MyCoAI 经 `CLAUDE_ENV_FILE` 换回的本机代理 | 不变 |
| 挂点命令、stdio 类型的 MCP 服务器 | claude.exe 起的子进程 | 继承 claude.exe 的环境，带票据的 `HTTPS_PROXY` 也在里面；它们访问第三方主机会被 403 | 挂点与 stdio MCP 启动时读不读 `CLAUDE_ENV_FILE`，待两边联调实测；不读的话，票据出现在这些本机同用户子进程的环境里，记作已知限制 |

### 6.3 白名单与 NO_PROXY 的口径（两边同一份）

1. **中转白名单不加主机**。中转只为两个官方主机解开 TLS、换凭据，不做通用代理（台账 1077）。第三方流量不该从中转机出口出去。
2. **官方家的主机永不进 NO_PROXY**：后缀 `anthropic.com`、`claude.ai`、`claude.com` 一个都不能有。直连就是从用户本机出口带着站点设备编号去官方，等于第二台设备（D-011）。MyCoAI 启动前核对最终生效的 NO_PROXY（含用户设置 env 覆盖后的值），命中就不启动。
3. **第三方主机：两边已定走甲**（2026-11-24 对齐）：接受官方模式的思域会话里第三方联网功能失败，MyCoAI 在启动时读出哪些功能受影响；NO_PROXY 只放本机直连就能到的地址（本机与局域网段）。乙（MyCoAI 的回环转接器扩成分流）等用户真用到再立单，那会改变线上传输，要升协议版本并实测。

## 7. 令牌与刷新

1. 官方模式期间 `.credentials.json` 里只有个人令牌，`expiresAt` 为 null，没有 `refreshToken`。客户端只在有刷新令牌、而且令牌到期时才刷新；到期时刻为 null 视为不过期；收到 401 强制刷新也要有刷新令牌，没有就不刷新（openclaude `src/services/oauth/client.ts:344`、`src/utils/auth.ts:1478-1500`）。所以思域会话期间客户端不刷新。152 的真实轮已在新启动、后台任务、恢复约 13 分钟内验证这种文件可用；更长时间与 401 之后的表现待两边联调实测。
2. 刷新地址 `platform.claude.com`（openclaude `src/constants/oauth.ts:91`）不在白名单。就算有进程想经代理刷新，CONNECT 回 403，到不了官方。
3. 用户本人的官方令牌：切到官方模式时，默认位置里的凭据文件已换成个人令牌（用户本人的凭据由 MyCoAI 先存进它自己的账户库），思域会话期间文件里没有用户本人的刷新令牌，不会经代理刷新。
4. 用户本人的凭据会不会经过中转：只在「切换时进程没退」的竞态下会，见第 4.5 节第 1 条；中转本地 401，不转发。所以「先停进程、再换文件」是硬要求。
5. 官方令牌只在中转机，由中转机上的 Claude Code 续期（D-009「一份登录只放一处用」）；个人令牌只在用户这台电脑。两者互不相干。标准模式下第三方密钥同样只在中转机。

## 8. 同一身份、很多会话一起发

1. 一个 Claude Code 进程一个思域会话。同一身份、同一台电脑同时建多个会话没有问题：每个会话有自己的 sessionId、票据、租约和隧道名额，互不影响，停一个不影响别的。
2. 上限：每个使用者同时存活 8 个会话（官方与标准一起算），第 9 个回 429 `too-many-sessions`，读句「你同时开着的思域已经有 8 个。请先关掉不用的，再启动。」；每个会话同时 16 条代理隧道，超了：
   - 版本 2 会话：429 `too-many-tunnels`，读句「这个思域会话同时打开的连接已经有 16 条，这次连接没有建立。请稍后再试；一直这样请重新连接思域。」。
   - 版本 1 会话：照旧 407 `proxy-authentication`「思域会话无效或已结束，请重新启动思域。」（过渡期逐字不变）。
   - 没有按官方账户的并发上限。
3. 额度：绑同一个官方账户的所有会话共用这个账户的官方额度，由官方计算。官方回 429 时原样转给客户端（带 `Retry-After`），中转不冷却、不换号；中转只对官方的 401、403 记「这一代凭据被拒」。每次推理、估算、模型目录都按使用者与账户记用量。标准模式的额度、冷却与自动切换照旧接法。
4. 一个目录的约束：同一台电脑同一时刻所有官方模式的思域会话绑同一个账户（第 4.5 节第 2 条）。

## 9. 后端选择与切换（版本 2）

1. **选哪一家**：照中转机上游页「在用」的那一家，不另设思域专用的选择。
   - 在用的是能用于思域的官方账户（未损坏、官方登录、未导出、检测已通过；与账户列表同一个判据）→ 官方模式，绑这一家。
   - 在用的是独立 API 或兼容中转 → 标准模式。
   - 都不是（没配置在用上游、在用的是贴令牌的官方账户、在用的官方账户没通过检测或已导出）→ 503 `no-usable-upstream`，不打回，要管理员处理。
2. **什么时候发现换了**：版本 2 会话每次续租时，中转重算一次后端此刻的选择；与会话绑定的不同（模式不同，或官方模式的账户主键不同）就以 `account-switched` 结束这个会话，这次续租回 409 `account-switched` 加打回 `recreate`。原因不论是管理员切换在用、在用那一家检测失败、导出、删除还是损坏，都走同一条路。标准模式之间换家不算换：旧接法自己跟着在用走。MyCoAI 每 40 秒续租一次，所以最晚约 40 秒得知；在这之前旧会话照旧可用。
3. **账户退役**（删除、导出）时，绑它的版本 2 会话立刻以 `account-switched` 结束（版本 1 照旧 `official-account-unavailable`）。
4. **官方 ↔ 标准**：用同一个 `account-switched` 加 `recreate`。MyCoAI 停掉旧进程，用新的 `requestId` 重建会话，看新回应的 `mode`：官方就写三样、按第 4.2 节起进程；标准就还原默认位置、按第 4.3 节起进程。
5. **版本 1 会话不受后端选择影响**：版本 1 由客户端选账户，续租不核对后端选择，与文本版本 1.1 逐字相同。

## 10. 打回（版本 2）

用户的原则（2026-11-24）：中转核对客户端发来的对不对，不对就告诉客户端下一步做什么，客户端照做、重发，中转核对通过就放行，无人值守；只在两方协议范围内。

### 10.1 打回只说「下一步做什么」，对的值只从建会话回应一处给

- `recreate`：MyCoAI 结束本地会话（停进程），用新的 `requestId` 重新建会话；有 `expectedIdentityVersion` 就带上它，没有就带上一次建会话回应里的 `session.siteIdentityVersion`（标准模式带 null）；按新回应准备、再起进程。
- `wait`：过 `retryAfterSeconds` 秒再建会话，用于维护中、名单暂时读不到这类会自己好的情形。
- 没有 `remedy` 的拒绝要人来处理，MyCoAI 停下并读出读句。
- 身份、账户资料、代理接入只从建会话回应来，拒绝体里不放：两个来源迟早不一致。

### 10.2 写死的四条

1. **打回能改的东西只限白名单**。自动处理只会改这些：`.claude.json` 的 `userID` 与 `oauthAccount`、`.credentials.json` 的 `claudeAiOauth` 三项资料、下一次建会话的 `expectedIdentityVersion`、进程环境里第 4.2、4.3 节那两张表的变量（代理地址、票据、代理 CA、标准 API 地址）。**不许**改、也不许下发：个人令牌（它是这条通道的认证本身，失效了要人来换）、任何官方令牌与第三方密钥、用户本地的权限、技能、挂点、设置、插件、MCP 配置、别的环境变量、任何文件路径或命令。中转侧由用例 `siyu: protocol text version 2 response keys are the whitelist` 守住：版本 2 每一种回应写出的键全集等于下面「契约表:版本2键」那一行。回应里出现表外的键，MyCoAI 当协议错误处理：停下、读出读句，不照做。
2. **重试有上限**（MyCoAI 守）。同一个启动方案在一次运行里，同一个原因码连续自动处理最多 2 次，所有原因码合计 10 分钟内最多 3 次；到上限就停下，读出最后一次的读句，并说「已经自动重试了几次」。`identity-mismatch` 只自动处理 1 次：文件已经按建会话回应写过还不符，说明是别的原因，要人看。
3. **每次打回都留痕**。中转在日志里记事件 `SiyuRemedy`（警告级，进滚动文件）：时刻、使用者主键、令牌主键、会话编号（建会话时为空）、原因码、下一步、给出的身份代次；不记令牌、票据、邮箱或资料原文。MyCoAI 在活动记录里记：时刻、原因码、做了什么、改了哪几个键、改前改后的身份代次与资料代次。
4. **只认验证过的控制通道**。只有控制面的回应才可能带 `remedy`：经站点可信 HTTPS（证书链到 D-002 的站点私有 CA，主机是配置里的站点），并且这次请求带了个人令牌。代理通道上的拒绝（claude.exe 收到的那些）永远不带 `remedy`，MyCoAI 也不去解析它们当指令；官方的回应更不算。

### 10.3 哪些原因码可以自动处理

只有版本 2 的建会话与续租回应会带打回。续租时中转按会话绑定的版本判断；会话已经找不到（中转重启后内存里的会话全没了）时，按续租请求声明的 `clientProtocolVersion` 判断，所以版本 2 客户端续租要带它。

<!-- 契约表:打回 -->
| 原因码 | 下一步 | 说明 |
| --- | --- | --- |
| identity-version-stale | recreate | 带 `expectedIdentityVersion` |
| profile-changed | recreate | 新回应里是新资料 |
| lease-expired | recreate | 只新开一轮；结果不明的请求不重放 |
| service-stopping | recreate | 同上 |
| stopped-by-administrator | recreate | 同上 |
| session-not-found | recreate | 中转重启后续租回 404；草案 0.1 没有这一条，订单 180 补上，否则重启就要人管 |
| account-switched | recreate | 第 9 节 |
| identity-mismatch | recreate | MyCoAI 只自动 1 次 |
| official-account-unavailable | recreate | 版本 2 的会话不再以它结束（改记 `account-switched`），留着兜底 |
| official-account-gone | recreate | 同上 |
| site-maintenance | wait | 30 秒；维护完成后建会话可能再拿到 `identity-version-stale`，照它重建 |
| access-database-unavailable | wait | 30 秒 |
| usage-unavailable | wait | 30 秒；实际只在代理通道出现，留着兜底 |

其余（`personal-token-invalid`、`official-reauthentication-required`、`official-expiry-unknown`、`version-unsupported`、`protocol-unsupported`、`too-many-sessions`、`no-usable-upstream`、`siyu-disabled`、`profile-*`、`request-*`、`stopped`、格式码）不自动处理：读出读句，等人。

### 10.4 版本 2 回应的键全集（白名单）

键按点号连成路径；`oauthAccount`、`credentialMetadata` 是官方资料原字节，只算一个键；建会话回应里的 `session` 是会话视图，按会话视图那一行查。会话视图一行列的是已结束会话的键（键最全），存活时没有 `closeCode`、`closeSentence`。

<!-- 契约表:版本2键 -->
| 回应 | 键 |
| --- | --- |
| 建会话·官方 | session、launchIdentity、launchIdentity.deviceId、launchIdentity.oauthAccount、launchIdentity.credentialMetadata、proxy、proxy.endpoint、proxy.authentication、proxy.ticket、proxy.authorityPem、proxy.authoritySha256 |
| 建会话·标准 | session、standard、standard.baseUrl、standard.protocol、standard.authentication |
| 会话视图·官方 | serverInstanceId、sessionId、userId、tokenId、tokenVersion、mode、officialAccountId、siteIdentityVersion、profileVersion、clientProtocolVersion、claudeVersion、entrypoint、requestStrategy、transport、state、lease、lease.expiresAt、lease.renewAfterSeconds、closeCode、closeSentence |
| 会话视图·标准 | serverInstanceId、sessionId、userId、tokenId、tokenVersion、mode、clientProtocolVersion、claudeVersion、entrypoint、requestStrategy、transport、state、lease、lease.expiresAt、lease.renewAfterSeconds、closeCode、closeSentence |
| 拒绝·recreate | type、error、error.type、error.code、error.message、error.remedy、error.remedy.action、error.remedy.expectedIdentityVersion |
| 拒绝·wait | type、error、error.type、error.code、error.message、error.remedy、error.remedy.action、error.remedy.retryAfterSeconds |
| 拒绝·不打回 | type、error、error.type、error.code、error.message |

拒绝·recreate 一行是带期望代次的那种（`identity-version-stale`）；别的 recreate 没有 `error.remedy.expectedIdentityVersion`。

### 10.5 「后端换了账户，客户端自动接着走」的两种情形

- **新开一轮**：旧会话结束 → MyCoAI 先停掉所有用默认位置的 Claude Code 进程（第 4.5 节第 1 条）→ 建会话、按新回应准备 → 起新进程，从新对话开始。版本 2 只做这一种。
- **原会话续接**：同一份对话记录（`--resume`）换到另一个官方账户接着发。能不能接，取决于 MyCoAI 第 4 批「续接与签名实验」（对话里已有的思考块签名、缓存在换了账户后是否仍被接受）。实验出结论前不做；结论是能接，再升文本版本写进来。

### 10.6 为什么后端换账户走打回，不放宽核对

放宽核对（中转认「一池可用账户」）时，中转不改正文（订单 152），发给官方的正文账户段是旧账户，凭据却是新账户：官方看到正文自报的账户与凭据的主人不是同一个，原生客户端从来不会这样发，违背 D-013，也是 D-009 要管的客户端风险。打回让客户端按新回应重写身份，此后正文账户段与官方凭据属于同一个账户，与原生用户换账户登录一样。

## 11. 多账户同时用：现在不做

若以后要在一台电脑上同时用思域和用户本人的官方账户，就不能把思域身份写进同一个默认位置；而只靠环境变量交令牌时，正文里的身份仍是用户本人的，会被中转的身份核对拒绝（403 `device-mismatch` 或 `account-mismatch`）。中转不改正文（订单 152），所以这条现在不做：同一台电脑同一时刻只有一个 Claude 账户在用。

## 12. 过渡期兼容

- 中转同时认版本 1 与版本 2。版本 1 客户端（带 `officialAccountId`、不认 `remedy`）的行为与文本版本 1.1 逐字相同：会话视图没有 `mode`；`identity-version-stale` 带 `currentIdentityVersion`、不带 `remedy`；隧道满回 407；身份不符只拒这一次、不结束会话；续租不核对后端选择；账户退役记 `official-account-unavailable`。用例 `siyu: version 1 clients behave exactly as before version 2` 与 `siyu: control plane writes version 2 shapes and keeps version 1 bytes` 守住。
- 独立目录与一个目录的客户端在中转眼里没有区别（第 5 节）。
- MyCoAI 换到版本 2 之后，版本 1 留着不删，等两边总监确认家里每台电脑都换过去再议。

## 13. 两边联调清单（版本 2）

两边的契约用例都照这份文本写；真机联调用装着当前中转的真实站点。官方模式的每一条都要独立抓包（D-011：设备编号、客户端标识、出口、续期四样无差异）。

| 序号 | 动作 | 期望 |
| --- | --- | --- |
| 1 | 在用是官方账户，版本 2 建会话（不带账户，首次 `expectedIdentityVersion` 为 null） | 201，`session.mode` 为 `official`，`officialAccountId` 是在用那一家；三样写进默认位置；起进程后第一次 CONNECT 200 |
| 2 | 官方模式发一句 | 官方 200；中转出口的正文与客户端发出的逐字节相同，只换 Authorization |
| 3 | 管理员把在用切到独立 API | 最晚一次续租（约 40 秒）回 409 `account-switched` 加 `remedy.action` 为 `recreate`；重建得到 `mode` 为 `standard` 与 `standard.baseUrl` |
| 4 | 标准模式发一句 | 经标准上游 200，用量记在使用者名下（管理页用量） |
| 5 | 管理员切回官方账户 | 续租 409 `account-switched` 加 `recreate`；重建回到官方模式，默认位置重写三样 |
| 6 | 管理员切到另一个官方账户 | 同上，新回应里是新账户的 `oauthAccount`，正文账户段是新账户；旧会话的连接器发现集合不带过来 |
| 7 | 身份代次变更（管理员在中转机更换设备编号） | 会话以 `site-maintenance` 结束（打回 `wait` 30 秒）；带旧代次建会话回 409 `identity-version-stale`，`remedy.expectedIdentityVersion` 是新代次；重建后正文设备段是新编号 |
| 8 | 中转重启 | 续租（带 `clientProtocolVersion: 2`）回 404 `session-not-found` 加 `recreate`；重建成功 |
| 9 | 每个可自动处理的原因码各一条（第 10.3 节表） | 照 `recreate` 或 `wait` 处理后重建成功；中转日志里各有一条 `SiyuRemedy` |
| 10 | 白名单外的键（负向，假中转） | 回应里多一个键：MyCoAI 停下、读出读句，不照做 |
| 11 | 到上限（负向，假中转） | 同一原因码连续 3 次：第 3 次停下并读出「已经自动重试了 2 次」 |
| 12 | 代理通道上的拒绝（负向） | claude.exe 收到的 403、407、429 不当指令，MyCoAI 不解析 |
| 13 | 版本 1 客户端（MyCoAI 175） | 与文本版本 1.1 的联调清单逐条结果相同 |
| 14 | 切换竞态、用户设置冲突、第三方主机（负向） | 同文本版本 1.1 第 11 节第 7、8、9 条 |

## 14. 部署与回退

- 版本 2 的中转改动由订单 180 部署到用户在用的虚拟机正式中转；部署时刻、提交与抓包结果写在订单 180 的结果里，部署后在这里补一行。
- **部署前提**：中转机的项目设置里打开「旧接法官方请求关闭」，否则标准模式的请求可能被旧接法的自动切换送到官方账户（第 4.3 节）。
- **回退**：在虚拟机上停服务，把程序目录换回提交 cf474c7 的发布物，再启动。回退后版本 1 客户端照常（版本 1 两版逐字相同）；版本 2 客户端建会话会收到 426 `protocol-unsupported`，MyCoAI 应读出读句、按版本 1 重试或停下。中转会话只在内存里，回退与部署都会让在用的会话失效（续租回 404），客户端重新连接即可。

## 15. 本地拒绝一览

官方收不到这些请求。控制面与代理入口回同一种 JSON 形状（第 3.4 节）。会话已结束时，查询、续租与准入一律回 409，原因码是会话的结束原因。

<!-- 契约表:拒绝 -->
| 原因码 | 状态码 | 读句 |
| --- | --- | --- |
| stopped | 409 | 思域已停止。未确认完成的操作不会自动重发。 |
| personal-token-invalid | 401、409 | 思域的个人令牌已失效。请更新此启动配置的令牌，再启动。 |
| official-account-unavailable | 409 | 这个官方账户在中转机上已不可用，思域已停止。请在 MyCoAI 改选别的账户。 |
| site-maintenance | 503、409 | 思域因设备维护已停止。维护完成后请重新启动，原会话不会自动换号。 |
| lease-expired | 409 | 思域会话已结束，请重新启动；未确认完成的操作不会自动重发。 |
| profile-changed | 409 | 这个官方账户的资料变了，思域已停止。请重新启动思域，让它带上新的资料。 |
| official-reauthentication-required | 503、409 | 此官方账户需要重新登录。请在中转机管理页重新登录该账户，再启动思域。 |
| service-stopping | 409 | 中转服务已重启，思域会话已结束，请重新启动；未确认完成的操作不会自动重发。 |
| stopped-by-administrator | 409 | 管理员在中转机上结束了这个思域会话。需要时请重新启动思域；未确认完成的操作不会自动重发。 |
| account-switched | 409 | 中转机换了在用的上游，这个思域会话已结束。重新连接思域就会用上新的上游；未确认完成的操作不会自动重发。 |
| identity-mismatch | 409 | 这台电脑发来的请求身份和中转机给的不一致，思域会话已结束。重新连接思域会重新准备这台电脑的配置；再次不一致请检查是否有别的程序改了 Claude Code 的配置。 |
| identity-version-stale | 409 | 中转机的设备身份已经更新，这份思域配置需要重新准备。请重新连接思域。 |
| no-usable-upstream | 503 | 中转机此刻没有能给思域用的在用上游。请管理员在中转机上游页把一家测试通过的上游切换为在用，再重新连接思域。 |
| request-invalid | 400 | 思域启动请求的格式不对。请升级 MyCoAI 后再试。／续租序号必须从 1 开始。请升级 MyCoAI 后再试。 |
| request-conflict | 409 | 同一个请求编号带了不同的内容，思域没有创建会话。请升级 MyCoAI 后重新启动。 |
| session-closed | 409 | 同 lease-expired 的读句 |
| session-not-found | 404 | 找不到这个思域会话，可能已经结束很久了。请重新启动思域。 |
| too-many-sessions | 429 | 你同时开着的思域已经有 {MaxSessionsPerUser} 个。请先关掉不用的，再启动。（现为 8） |
| too-many-tunnels | 429 | 这个思域会话同时打开的连接已经有 {MaxTunnelsPerSession} 条，这次连接没有建立。请稍后再试；一直这样请重新连接思域。（现为 16，只对版本 2 会话） |
| protocol-unsupported | 426 | 这个 MyCoAI 的思域协议与中转机不一致。请把 MyCoAI 与中转更新到配套版本。 |
| version-unsupported | 426 | 此 Claude Code 版本尚未通过思域兼容验证。请使用已支持版本或选择其他启动方式。 |
| access-database-unavailable | 503 | 中转机暂时读不到使用者名单，思域没有处理这次请求。请稍后再试。 |
| usage-unavailable | 503 | 中转暂时记不下用量，这次请求没有发给官方。请稍等一会儿再试。 |
| mcp-not-discovered | 403 | 思域还没拿到这个账户的官方连接器清单，这次连接器请求没有转发。请重新启动思域会话，Claude Code 启动时会重新取清单。 |
| mcp-connector-unknown | 403 | 这个官方连接器不在这个账户当前的连接器列表里，思域没有转发。请在 Claude Code 里重新加载连接器后再试。 |
| device-mismatch | 403 | 使用者 {名字} 这台电脑发来的请求设备段和中转机的设备编号不一致，思域没有发给官方。请在 MyCoAI 里重新连接思域，让它重新准备这台电脑的配置。 |
| account-mismatch | 403 | 使用者 {名字} 这台电脑发来的请求账户段和这个官方账户的编号不一致，思域没有发给官方。请在 MyCoAI 里重新连接思域，让它重新准备这台电脑的配置。 |
| identity-unsupported | 403 | 使用者 {名字} 这台电脑发来的请求里没有可核对的设备与账户信息，思域没有发给官方。请在 MyCoAI 里重新连接思域，让它重新准备这台电脑的配置。 |
| profile-missing | 503 | 这个官方账户还没有账户资料。请在中转机管理页对这个账户获取一次模型，再启动思域。／中转机上这个官方账户的{哪一部分}缺少「{字段}」。请在中转机管理页对这个账户获取一次模型，再启动思域。 |
| profile-incomplete | 503 | 这个官方账户在中转机上的资料还不完整，思域暂不能用它。请在中转机管理页重新登录该账户，再启动思域。 |
| profile-malformed | 503 | 中转机上这个官方账户的{哪一部分}已损坏／不是预期的格式／里有重复的项「{字段}」。请在中转机管理页对这个账户获取一次模型，再启动思域。 |
| profile-unverified | 503 | 中转机上这个官方账户的{哪一部分}里有没核对过的项「{字段}」，思域暂不能用这个账户。请先在中转机完成 Claude Code 版本兼容验证。 |
| official-expiry-unknown | 503 | 这个官方账户的凭据没有写到期时间，思域不能确认它仍有效。请在中转机管理页重新登录该账户，再启动思域。 |
| official-account-gone | 503 | 这个官方账户已经删除或搬走，思域不能再用它。请在 MyCoAI 改选别的账户。 |
| account-configuration | 503 | 中转机私有配置的固定说明，原样带出 |
| not-found | 404 | 思域控制面没有这个地址。请升级 MyCoAI 后再试。 |
| siyu-disabled | 503 | 这台中转还没有打开思域。请管理员在中转的项目设置里勾选「接受新的思域会话」，然后重启中转服务。 |
| content-type | 415 | 思域控制面只收 JSON。请升级 MyCoAI 后再试。 |
| body-too-large | 413 | 思域控制面的请求太大。请升级 MyCoAI 后再试。（代理内层请求体超限也用这个码，读通用句） |
| request-malformed | 400 | 思域控制面看不懂这次请求的内容。请升级 MyCoAI 后再试。 |
| method | 405 | 通用句 |
| target | 403 | 思域只连接官方的两个地址，这个目标不在合同内。 |
| proxy-authentication | 407 | 思域会话无效或已结束，请重新启动思域。 |
| early-data | 400 | 通用句 |
| redirect-refused | 502 | 官方要求跳转到别的地址，思域不在合同外跟随，这次请求没有完成。 |

通用句：「思域拒绝了这次请求，官方没有收到它。请重新启动思域后再试。」

`official-account-unavailable` 与 `official-account-gone` 的「请在 MyCoAI 改选别的账户」只对版本 1 成立；版本 2 不选账户，会话改以 `account-switched` 结束，建会话时没有可用的就是 `no-usable-upstream`。

HTTP 报文格式不在合同内时回下面的格式码，读通用句，回完关闭这条连接。请求方向（客户端发来的）原样用格式码；回应方向（官方回来的）在格式码前加 `upstream-`、状态码一律 502，读句「官方的回应无法核对，这次请求的结果未知，没有自动重发。」。

<!-- 契约表:格式 -->
| 格式码 | 状态码 |
| --- | --- |
| head-too-large | 431 |
| head-truncated | 400 |
| request-line | 400 |
| head-trailing-bytes | 400 |
| obs-fold | 400 |
| header-name | 400 |
| header-value | 400 |
| bare-line-ending | 400、502 |
| content-length | 400 |
| version | 505 |
| transfer-encoding | 501、502 |
| expect | 417 |
| upgrade | 400、502 |
| length-required | 411 |
| body-too-large | 413、502 |
| authorization | 400 |
| host | 400 |
| route | 403 |
| status-line | 502 |
| transfer-encoding-and-length | 502 |
| chunk-terminator | 502 |
| trailers-too-large | 502 |
| chunk-size | 502 |
| chunk-extension | 502 |
| line-too-long | 502 |
