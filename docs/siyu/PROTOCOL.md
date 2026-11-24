# 思域协议：HouseholdRelay 与 MyCoAI 的唯一依据

文本版本：1.1　线上协议版本：1　日期：2026-11-24

这份文件是思域两边对接的唯一依据。两边以后改协议，都先改这一份，再各自改代码。

- HouseholdRelay 一侧：用例 `siyu: protocol text matches the code` 读这份文件里标了「契约表」的表格，与代码逐项对照，改了一边没改另一边就失败。
- MyCoAI 一侧：假中转 `FakeSiyuRelay` 的回应形状、原因码与状态码照这份文件写契约用例。
- 设计历史：`artifacts/order-152/design/mycoai-contract.md` 与 `relay-design.md` 是 2026-11-22 定稿时的设计与依据，留作历史；与本文冲突时以本文为准。
- 「打回」协商还是草案，在同目录 [REMEDY-DRAFT.md](REMEDY-DRAFT.md)，两边总监对齐前不生效，也不实现。

## 变更记录

| 文本版本 | 日期 | 线上协议版本 | 改了什么 | 来源 |
| --- | --- | --- | --- | --- |
| 1.0 | 2026-11-22 | 1 | 定稿：原生文件认证、受认证 HTTPS 代理、正文原样转发，两个官方主机 | 订单 152 |
| 1.0.1 | 2026-11-23 | 1 | 加账户列表口 `GET /v1/siyu/accounts`；写明代理凭据字符集 | 提交 2fbad9c |
| 1.1 | 2026-11-24 | 1 | 客户端身份改写进用户本人的默认位置（一个目录）；中转对客户端的依赖清单；用户本人配置经过代理的口径；令牌刷新；同一身份多会话；多账户同时用的限制；「打回」草案另起文件。线上协议不变，中转代码不变 | 订单 174 |

## 1. 两边各管什么

- **中转**：控制面（建会话、查询、续租、停止、账户列表）、代理入口、逐请求准入与核对、把个人令牌换成官方令牌、官方续期、用量。中转只看线上字节，不读客户端电脑上的任何文件。
- **MyCoAI**：调控制面建会话、把身份与个人令牌写进文件、起 Claude Code 进程并设环境、续租、停止、读屏状态。
- **请求正文**：由客户端的 Claude Code 生成，中转一个字节不改（台账「中转不改请求正文」）。
- **官方账户还是普通上游、换成哪一个**：按 2026-11-24 用户批示，只有后端知道、后端决定，客户端不判断。今天的线上协议版本 1 仍要求建会话时带 `officialAccountId`（客户端从账户列表里选），与这条批示不一致；改法写在 [REMEDY-DRAFT.md](REMEDY-DRAFT.md) 第 5 节，作协议版本 2 的候选，两边总监对齐前版本 1 照旧。

## 2. 线上数值

全部是版本 1 的固定值，唯一定义处是 `src/Core/Siyu/SiyuContract.cs` 与 `src/Infrastructure/ExternalSources/Siyu/SiyuProxyLimits.cs`，不交给用户配置。租约、限额与保留期还没在目标 Windows 上测过长时间断线和睡眠恢复，双机验收时实测，不合就改代码并在这里升文本版本。

<!-- 契约表:数值 -->
| 名字 | 值 | 含义 |
| --- | --- | --- |
| ProtocolVersion | 1 | 建会话必须带的 `clientProtocolVersion` |
| RequestStrategy | nativePreserve | 唯一请求策略：正文原样 |
| Transport | nativeHttpsProxy | 唯一传输：原生 HTTPS 代理 |
| SupportedClaudeVersions | 2.1.325 | 已验证的 Claude Code 版本，其余拒建会话 |
| SupportedEntrypoints | sdk-cli | 已验证的入口，其余拒建会话 |
| LeaseDurationSeconds | 120 | 一次续租给的存活时长 |
| LeaseRenewAfterSeconds | 40 | 建议续租间隔 |
| MaxSessionsPerUser | 8 | 每个使用者同时存活的会话上限 |
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

只经站点可信 HTTPS（对外 8787，私有 CA 由 D-002 装好信任），前缀 `/v1/siyu`。认证只认 `Authorization: Bearer <个人令牌>`。所有回应带 `Cache-Control: no-store`。思域没打开时前缀下每个请求都回 503 `siyu-disabled`。

| 方法与路径 | 请求 | 成功 |
| --- | --- | --- |
| `GET /v1/siyu/accounts` | 只要令牌 | 200 `{"accounts":[{"officialAccountId":<整数>,"displayName":"<名称>"}]}`；一个都没有是空数组 |
| `POST /v1/siyu/sessions` | JSON：`requestId`、`officialAccountId`、`expectedIdentityVersion`（首次可为 null）、`clientProtocolVersion`、`claudeVersion`、`binarySha256`、`entrypoint`、`transport` | 201，见下面回应形状；同一 `requestId` 同样内容重试回同一会话与票据 |
| `GET /v1/siyu/sessions/{sessionId}` | 只要令牌（同一使用者任一当前有效令牌） | 200 会话视图；不续租 |
| `PUT /v1/siyu/sessions/{sessionId}/lease` | JSON：`leaseSequence`（从 1 起单调） | 200 会话视图；同序号或更小只回现值，不重复延长；只认创建它的那一版令牌 |
| `DELETE /v1/siyu/sessions/{sessionId}` | 只要令牌 | 已结束 204；资源还没归还完 202 加会话视图（`state` 为 `closing`）；重复停止幂等 |

建会话回应：

```text
{
  "session": <会话视图>,
  "launchIdentity": { "deviceId": "<站点设备编号>", "oauthAccount": {<官方账户资料原字节>}, "credentialMetadata": {"scopes":[…], "subscriptionType":…, "rateLimitTier":…} },
  "proxy": { "endpoint": "<代理地址，不含凭据>", "authentication": "basic-session-ticket", "ticket": "<票据>", "authorityPem": "<代理 CA 公钥证书>", "authoritySha256": "<代理 CA 证书 DER 的 SHA256>" }
}
```

会话视图：`serverInstanceId`、`sessionId`、`userId`、`tokenId`、`tokenVersion`、`officialAccountId`、`siteIdentityVersion`、`profileVersion`、`clientProtocolVersion`、`claudeVersion`、`entrypoint`、`requestStrategy`、`transport`、`state`（`active`、`closing`、`closed`）、`lease.expiresAt`、`lease.renewAfterSeconds`，结束了另带 `closeCode`、`closeSentence`。

拒绝回应（控制面与代理入口同一形状）：

```text
{ "type": "error", "error": { "type": "siyu_refused", "code": "<原因码>", "message": "<中文读句>", "currentIdentityVersion": <只有 identity-version-stale 才有> } }
```

代理凭据：`HTTPS_PROXY=https://<sessionId>:<ticket>@<endpoint 的主机与端口>`。sessionId 是 32 位小写十六进制，ticket 是 32 个随机字节去掉补位的 base64url（43 个字符），两样都只含 `[A-Za-z0-9_-]`，`Uri.EscapeDataString` 后不变；中转只做 Basic 的 base64 解码，不做百分号解码。

## 4. 客户端要准备的东西

### 4.1 写进用户本人默认位置的三样（文本版本 1.1 起）

以前 MyCoAI 给每个思域方案单独建一个 `CLAUDE_CONFIG_DIR`。从这一版起不再有独立目录：使用思域时，像切换官方账户一样，把下面三样写进用户本人的默认位置；切回用户自己的账户时还原。

| 文件 | 键 | 值从哪来 |
| --- | --- | --- |
| `%USERPROFILE%\.claude.json` | 顶层 `userID` | 建会话回应 `launchIdentity.deviceId` |
| `%USERPROFILE%\.claude.json` | 顶层 `oauthAccount` | 建会话回应 `launchIdentity.oauthAccount`，原字节 |
| `%USERPROFILE%\.claude\.credentials.json` | `claudeAiOauth` | `accessToken` 填个人令牌，`expiresAt` 为 null，不写 `refreshToken`；再加 `launchIdentity.credentialMetadata` 的三项 |

`.claude.json` 里别的键、`settings.json`、技能、挂点、插件、MCP 配置、历史都不动，它们从此在思域会话里也生效。官方令牌永远不下发。

### 4.2 进程环境

| 变量 | 值 | 为什么 |
| --- | --- | --- |
| `HTTPS_PROXY` | 见第 3 节「代理凭据」 | Claude Code 的推理、账户附带接口、官方连接器三条网络栈都走它 |
| `NODE_EXTRA_CA_CERTS` | 建会话回应的 `authorityPem` 存成的文件 | 只给这个进程信任代理 CA，不装系统证书库 |
| `NO_PROXY` | 见第 6.3 节 | 不能含任何官方主机 |
| `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` | `1` | 关掉遥测、错误上报、自动更新、反馈；遥测里可能带各台电脑自己的编号 |
| `CLAUDE_ENV_FILE` | MyCoAI 写的脚本 | Bash 工具每条命令前执行：去掉带票据的代理变量和代理 CA，换回用户原有的代理设置 |

不再设 `CLAUDE_CONFIG_DIR`。启动前清掉会覆盖文件认证或改路由的继承变量（认证令牌、API 密钥、BASE_URL、认证 FD、provider 选择、自定义 OAuth 地址、大小写各种代理变量、`BUN_TLS_REFUSE_DISTRUSTED_KEYS` 等），清单以 `artifacts/order-152/design/launch-environment-validation.md` 的 34 项为底。

### 4.3 一个目录才有的规矩

1. **写身份文件之前，这台电脑上所有用默认位置的 Claude Code 进程都要先退出**，MyCoAI 之外在终端或编辑器里开的也算。依据：运行中的 Claude Code 按 `.credentials.json` 的修改时刻重读令牌、按 `.claude.json` 的修改时刻重读全局配置（openclaude `src/utils/auth.ts:1340`、`src/utils/config.ts:1284-1310`；openclaude 是分叉，真实 2.1.325 待两边联调实测）。没退出会出两种事：
   - 没设思域代理的旧进程读到个人令牌，直接发给官方：个人令牌落到官方手里，而且这次直连从用户本机出口出去，正文里已经是站点设备编号，等于官方看到第二台设备。中转看不到这次请求，拦不住，只能靠 MyCoAI 先停进程。
   - 切回用户自己的账户以后，还没停的思域进程读到用户本人的官方令牌，经代理发到中转。中转核对个人令牌不认，本地回 401，不转发给官方，也不记令牌的值；但用户本人的令牌已经到了中转机。
2. **同一台电脑同一时刻，所有思域会话必须绑同一个 `officialAccountId`**：身份文件只有一份。
3. **切回用户自己的账户**：先停全部思域会话（`DELETE`），确认进程退出，再还原三样。
4. **启动前查用户本人设置里的冲突项**，有就不启动，读出是哪一项、在哪个文件、怎么改；不替用户改设置：
   - `%USERPROFILE%\.claude\settings.json` 的 `env` 里出现下面任一个（不分大小写）：`HTTPS_PROXY`、`HTTP_PROXY`、`ALL_PROXY`、`NO_PROXY`、`NODE_EXTRA_CA_CERTS`、`NODE_TLS_REJECT_UNAUTHORIZED`、`ANTHROPIC_BASE_URL`、`ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`CLAUDE_CODE_OAUTH_TOKEN`、`CLAUDE_CODE_USE_BEDROCK`、`CLAUDE_CODE_USE_VERTEX`、`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`。依据：设置里的 `env` 在进程启动后整个盖到进程环境上（openclaude `src/utils/managedEnv.ts:142-155`），会把 MyCoAI 设的代理、证书、认证改掉。
   - 同一个设置文件里有 `apiKeyHelper`，或 `forceLoginMethod` 不是 `claudeai`。依据：有 apiKeyHelper、ANTHROPIC_AUTH_TOKEN、ANTHROPIC_API_KEY 时客户端不走官方登录态（openclaude `src/utils/auth.ts:105-142`），正文账户段变成空串（`src/services/api/claude.ts:568`），中转回 403 `account-mismatch`；带 `x-api-key` 的请求中转回 400 `authorization`。
   - 这一条在今天的独立目录里不存在（目录里的 `settings.json` 由 MyCoAI 管），是改成一个目录后新增的风险。真实 2.1.325 的覆盖顺序待两边联调实测；实测前按会覆盖处理。

## 5. 中转对客户端的全部依赖

中转只靠下面这些线上事实判断，每一项都写了中转在哪里用、缺了会怎样。它们在客户端从哪来，写在「客户端来源」一列。

<!-- 契约表:依赖 -->
| 序号 | 依赖 | 客户端来源 | 中转在哪里用 | 缺了或不对会怎样 |
| --- | --- | --- | --- | --- |
| 1 | CONNECT 带唯一 `Proxy-Authorization: Basic 会话编号:票据` | 环境变量 `HTTPS_PROXY` 里的凭据 | `src/Infrastructure/ExternalSources/Siyu/SiyuProxyConnectionHandler.cs:66`、`:96` | 407 `proxy-authentication` |
| 2 | 外层 TLS 信任站点证书 | D-002 装进 Windows 的站点私有 CA | `src/HouseholdRelay/Siyu/SiyuProxyHost.cs:44` | 客户端握手失败，请求到不了中转 |
| 3 | CONNECT 目标与 Host 逐字等于两个官方目标之一 | Claude Code 按代理语义自己生成 | `SiyuProxyConnectionHandler.cs:64`、`:65`；`src/Core/Siyu/OfficialTargets.cs:30` | 403 `target`；400 `host` |
| 4 | 内层 TLS 信任代理 CA | 环境变量 `NODE_EXTRA_CA_CERTS` | `SiyuProxyConnectionHandler.cs:81` | 客户端握手失败，失败关闭 |
| 5 | 内层 SNI 等于这条隧道的官方主机，ALPN 只协商 http/1.1 | Claude Code 自己生成 | `SiyuProxyConnectionHandler.cs:90`、`:91` | 连接直接关闭 |
| 6 | 内层 Host 等于隧道主机，方法与路径在路由白名单里 | Claude Code 自己生成 | `src/Infrastructure/ExternalSources/Siyu/SiyuInnerExchange.cs:61`、`:62`；`src/Core/Siyu/SiyuRoutes.cs:33` | 400 `host`；403 `route` |
| 7 | HTTP/1.1，请求体只用单个 Content-Length，不带 Transfer-Encoding、Expect、Upgrade | Claude Code 自己生成 | `src/Infrastructure/ExternalSources/Siyu/Http1RequestHead.cs:43-50` | 第 13 节格式码表里的状态码 |
| 8 | 恰好一个 `Authorization: Bearer <个人令牌>`，不带 `x-api-key` | `.credentials.json` 的 `claudeAiOauth.accessToken`；用户设置不能让客户端改走 API 密钥（第 4.3 节第 4 条） | `Http1RequestHead.cs:61-70` | 400 `authorization` |
| 9 | 这个个人令牌就是建会话的那一版 | 同上 | `src/Application/Services/Siyu/SiyuSessionService.cs:246-247`、`:380-384` | 401 `personal-token-invalid` |
| 10 | Messages 正文 `metadata.user_id` 的设备段等于站点设备编号 | `.claude.json` 顶层 `userID` | `SiyuSessionService.cs:360-372`；`src/Core/Relay/DeviceMetadataRewrite.cs:51-75` | 403 `device-mismatch` |
| 11 | 同一字段的账户段等于绑定账户的 `accountUuid` | `.claude.json` 顶层 `oauthAccount.accountUuid`，且客户端处于官方登录态（`scopes` 含 `user:inference`） | 同上 | 403 `account-mismatch` |
| 12 | `metadata.user_id` 是两种已支持格式之一，唯一、不重复 | Claude Code 自己生成 | 同上 | 403 `identity-unsupported` |
| 13 | 官方连接器编号在同账户当次成功发现的集合里 | 客户端先经思域发 `GET /v1/mcp_servers`（客户端按 `scopes` 决定发不发） | `SiyuSessionService.cs:249-262` | 403 `mcp-not-discovered`、`mcp-connector-unknown` |
| 14 | 建会话的协议版本、传输、Claude Code 版本与入口、二进制摘要、身份代次 | MyCoAI 填，不来自配置目录 | `SiyuSessionService.cs:99-100`、`:626-637` | 426 `protocol-unsupported`、`version-unsupported`；400 `request-invalid`；409 `identity-version-stale` |

**中转不依赖的东西**（产品代码里搜不到，`ClientLauncher.cs:67`、`IsolatedClaudeEnvironment.cs:23` 两处是中转机自己的 claude.exe，与客户端无关）：`settings.json` 的任何键、`CLAUDE_CONFIG_DIR`、`CLAUDE_ENV_FILE`、`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`、`.claude.json` 里 `userID` 与 `oauthAccount` 以外的键、`credentialMetadata` 的 `subscriptionType` 与 `rateLimitTier`、凭据文件的 `expiresAt` 与有无 `refreshToken`。它们影响的是客户端发不发、发成什么样，间接落到上表某一项上：`scopes` 决定第 11、13 项，`expiresAt:null` 与没有 `refreshToken` 决定客户端不刷新（第 7 节），非必要流量开关决定遥测发不发（第 6.2 节）。

**结论**：MyCoAI 把同样三样写进用户本人默认位置、进程环境照旧，今天的中转原样放行，不用改代码。中转眼里两种客户端没有区别：它只看线上字节，而两种客户端发出的字节一样。差的只在客户端一侧：用户本人设置里的冲突项（第 4.3 节第 4 条）和切换时没退出的进程（第 4.3 节第 1 条），这两件由 MyCoAI 在启动前挡住。

## 6. 经过代理的流量

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

| 流量 | 谁发、走哪 | 今天的结局 | 口径 |
| --- | --- | --- | --- |
| 推理、估算、模型目录、远程设置、限额、连接器发现、官方连接器 | claude.exe，经代理到两个官方主机 | 放行 | 不变 |
| 遥测、错误上报、自动更新、反馈 | claude.exe | 设了 `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` 就不发 | MyCoAI 必须设，且用户设置不能把它改掉（第 4.3 节第 4 条） |
| 官方家的其他主机或没登记的路径，例如令牌刷新 `platform.claude.com`、`claude.ai`、`api.anthropic.com` 上的资料刷新或网页抓取域名预检 | claude.exe，经代理 | 403 `target` 或 403 `route`，官方收不到 | 不加白名单，永不进 NO_PROXY。真实出现并影响功能时，按台账 1077 定稿的失效条件先实测再修订本文，不悄悄放宽 |
| 用户本人的远程 MCP 服务器（http、sse 类型）、插件市场（git 拉取）、网页抓取工具访问第三方网站 | claude.exe 或它直接起的 git，经代理 | CONNECT 403 `target`，这些功能失败：MCP 显示连接失败，插件装不上或更新不了，网页抓取把错误交给模型。具体表现待两边联调实测 | 见第 6.3 节 |
| Bash 工具里的命令（git、npm、dotnet、curl） | 子进程 | 走用户原有代理，照常可用。本会话实测：思域启动的会话里，子进程的 `HTTPS_PROXY` 是 MyCoAI 经 `CLAUDE_ENV_FILE` 换回的本机代理 | 不变 |
| 挂点命令、stdio 类型的 MCP 服务器 | claude.exe 起的子进程 | 继承 claude.exe 的环境，带票据的 `HTTPS_PROXY` 也在里面；它们访问第三方主机会被 403 | 挂点与 stdio MCP 启动时读不读 `CLAUDE_ENV_FILE`，待两边联调实测；不读的话，票据出现在这些本机同用户子进程的环境里，记作已知限制 |

### 6.3 白名单与 NO_PROXY 的口径（两边同一份）

1. **中转白名单不加主机**。中转只为两个官方主机解开 TLS、换凭据，不做通用代理（台账 1077）。第三方流量不该从中转机出口出去。
2. **官方家的主机永不进 NO_PROXY**：后缀 `anthropic.com`、`claude.ai`、`claude.com` 一个都不能有。直连就是从用户本机出口带着站点设备编号去官方，等于第二台设备（D-011）。MyCoAI 启动前核对最终生效的 NO_PROXY（含用户设置 env 覆盖后的值），命中就不启动。
3. **第三方主机**：NO_PROXY 的意思是「直连」，不是「走用户本机代理」。用户这里出境要靠本机代理的主机（GitHub、npm 等），放进 NO_PROXY 也连不上。所以在一个 Claude Code 进程里，用环境变量做不到「官方走思域、第三方走用户本机代理」。两条路待两边总监定：
   - 甲：接受思域会话里第三方联网功能失败，MyCoAI 在启动时读出哪些功能受影响；NO_PROXY 只放本机直连就能到的地址（今天已放的本机与局域网段）。
   - 乙：MyCoAI 的薄回环转接器（152 定稿保留的 `loopbackTunnel`）扩成分流：两个官方主机经思域，其余经用户本机代理。这改变线上传输，要协议版本 2 与实测。
   - 工程师建议先走甲，把用户实际用到的第三方联网功能列出来，再决定值不值得做乙。

## 7. 令牌与刷新

1. 思域期间 `.credentials.json` 里只有个人令牌，`expiresAt` 为 null，没有 `refreshToken`。客户端只在有刷新令牌、而且令牌到期时才刷新；到期时刻为 null 视为不过期；收到 401 强制刷新也要有刷新令牌，没有就不刷新（openclaude `src/services/oauth/client.ts:344`、`src/utils/auth.ts:1478-1500`）。所以思域会话期间客户端不刷新。152 的真实轮已在新启动、后台任务、恢复约 13 分钟内验证这种文件可用；更长时间与 401 之后的表现待两边联调实测。
2. 刷新地址 `platform.claude.com`（openclaude `src/constants/oauth.ts:91`）不在白名单。就算有进程想经代理刷新，CONNECT 回 403，到不了官方。
3. 用户本人的官方令牌：切到思域时，默认位置里的凭据文件已换成个人令牌（用户本人的凭据由 MyCoAI 先存进它自己的账户库），思域会话期间文件里没有用户本人的刷新令牌，不会经代理刷新。
4. 用户本人的凭据会不会经过中转：只在「切换时进程没退」的竞态下会，见第 4.3 节第 1 条；中转本地 401，不转发。所以「先停进程、再换文件」是硬要求。
5. 官方令牌只在中转机，由中转机上的 Claude Code 续期（D-009「一份登录只放一处用」）；个人令牌只在用户这台电脑。两者互不相干。

## 8. 同一身份、很多会话一起发

1. 一个 Claude Code 进程一个思域会话。同一身份、同一台电脑同时建多个会话没有问题：每个会话有自己的 sessionId、票据、租约和隧道名额，互不影响，停一个不影响别的。
2. 上限：每个使用者同时存活 8 个会话，第 9 个回 429 `too-many-sessions`，读句「你同时开着的思域已经有 8 个。请先关掉不用的，再启动。」；每个会话同时 16 条代理隧道，超了今天回 407 `proxy-authentication`，读句「思域会话无效或已结束，请重新启动思域。」（这句读句说的不是真实原因，已列为遗留，待后续单改）。没有按官方账户的并发上限。
3. 额度：绑同一个官方账户的所有会话共用这个账户的官方额度，由官方计算。官方回 429 时原样转给客户端（带 `Retry-After`），中转不冷却、不换号；中转只对官方的 401、403 记「这一代凭据被拒」。每次推理、估算、模型目录都按使用者与账户记用量。
4. 一个目录的约束：同一台电脑同一时刻所有思域会话绑同一个账户（第 4.3 节第 2 条）。

## 9. 多账户同时用：现在不做

若以后要在一台电脑上同时用思域和用户本人的官方账户，就不能把思域身份写进同一个默认位置；而只靠环境变量交令牌时，正文里的身份仍是用户本人的，会被中转的身份核对拒绝（403 `device-mismatch` 或 `account-mismatch`）。中转不改正文（订单 152），所以这条现在不做：同一台电脑同一时刻只有一个 Claude 账户在用。

## 10. 过渡期兼容

MyCoAI 切到一个目录之前，用独立目录的思域客户端照常能用。两种客户端写进文件的是同样三样、进程环境里代理与证书一样，生成的线上字节一样；中转只看线上字节（第 5 节），分不出两者，也不需要分。

## 11. 两边联调清单

两边的契约用例都照这份文本写；真机联调用装着当前中转的真实站点，每一条都要独立抓包（D-011：设备编号、客户端标识、出口、续期四样无差异）。

| 序号 | 动作 | 期望 |
| --- | --- | --- |
| 1 | 建会话（首次 `expectedIdentityVersion` 为 null） | 201，回应里三样写进默认位置；起进程后第一次 CONNECT 200 |
| 2 | 发一句 | 官方 200；中转出口的正文与客户端发出的逐字节相同，只换 Authorization |
| 3 | 身份代次变更（管理员在中转机更换设备编号） | 会话以 `site-maintenance` 结束；带旧代次建会话回 409 `identity-version-stale` 与 `currentIdentityVersion`；带新代次重建后正文设备段是新编号 |
| 4 | 换账户（停会话、用另一个 `officialAccountId` 建会话） | 新回应里的 `oauthAccount` 写进默认位置，正文账户段是新账户；旧会话的连接器发现集合不带过来 |
| 5 | 打回 | 草案对齐并实现之后才测，见 REMEDY-DRAFT.md 第 7 节 |
| 6 | 同一身份同时开 3 个会话 | 3 个会话各自发一句都成功；停其中一个，另两个不受影响 |
| 7 | 切换竞态（负向） | 先开一个不经思域的用户本人进程，不停它就切到思域：MyCoAI 应拒绝切换并读出原因 |
| 8 | 用户设置冲突（负向） | `settings.json` 的 `env` 里放一个 `ANTHROPIC_BASE_URL`：MyCoAI 应拒绝启动并读出哪一项 |
| 9 | 第三方主机（负向） | 用户本人配一个远程 MCP 服务器：CONNECT 403 `target`，记录 claude.exe 的表现 |

## 12. 部署与回退

文本版本 1.1 没有改中转的产品代码，用户在用的站点不用部署，也没有要回退的东西。「打回」落地、协议版本 2 那张单才有部署，到时在那张单写明部署时刻与回退办法。

## 13. 本地拒绝一览

官方收不到这些请求。控制面与代理入口回同一种 JSON 形状（第 3 节）。会话已结束时，查询、续租与准入一律回 409，原因码是会话的结束原因。

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
| identity-version-stale | 409 | 中转机的设备身份已经更新，这份思域配置需要重新准备。请重新连接思域。 |
| request-invalid | 400 | 思域启动请求的格式不对。请升级 MyCoAI 后再试。／续租序号必须从 1 开始。请升级 MyCoAI 后再试。 |
| request-conflict | 409 | 同一个请求编号带了不同的内容，思域没有创建会话。请升级 MyCoAI 后重新启动。 |
| session-closed | 409 | 同 lease-expired 的读句 |
| session-not-found | 404 | 找不到这个思域会话，可能已经结束很久了。请重新启动思域。 |
| too-many-sessions | 429 | 你同时开着的思域已经有 {MaxSessionsPerUser} 个。请先关掉不用的，再启动。（现为 8） |
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
