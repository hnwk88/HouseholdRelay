# 评估证据与官方资料

核查日期：2026-10-28（Asia/Shanghai）。代码基线：`9fff525`；开始核查及基线测试后的 Git 工作区均无业务改动。

## 本地代码证据

| 路径和核查位置 | 结论 |
| --- | --- |
| `HouseholdRelay/HouseholdRelay.csproj`，全部 | net10.0-windows，Web SDK，无 PackageReference |
| `HouseholdRelay/Program.cs`，Main/Help | 仅命令入口，无网页首次设置 |
| `HouseholdRelay/Configuration.cs:7`、`:47` | ClientAccess 只有名称/哈希/启用状态，名单要求 1 至 8 个，无稳定使用者实体 |
| `HouseholdRelay/Configuration.cs:59` | SHA-256 哈希及 32 字节随机令牌可复用 |
| `HouseholdRelay/Management.cs:15` | Initialize 调用 DetectUserAgent，生成私有 CA、TLS 和三份客户端包 |
| `HouseholdRelay/Management.cs:65` | SetAccount 仅在本机终端隐藏输入令牌，不支持网页授权或刷新 |
| `HouseholdRelay/Management.cs:126`、`:145` | 启动一次加载配置/凭据，只轮询客户端权限文件 |
| `HouseholdRelay/RelayServer.cs:17`、`:103` | 三类模型路由白名单；另处理健康、状态及 HEAD 预热 |
| `HouseholdRelay/RelayServer.cs:29`、`:36` | 正式上游地址写死；testUpstream 仅允许 127.0.0.1 HTTP |
| `HouseholdRelay/RelayServer.cs:141` | oauth 使用 Bearer 并附加 OAuth beta，api-key 分支使用 x-api-key |
| `HouseholdRelay/RelayServer.cs:153`、`:167`、`:172` | ResponseHeadersRead、响应头和正文流转发；没有用量采集和错误正文脱敏 |
| `HouseholdRelay/RelayServer.cs:181`、`:193` | completed 为内存累计，请求日志仅含元数据，不是数据库用量 |
| `HouseholdRelay/Admission.cs:12`、`:29` | 常量时间哈希鉴权、可替换权限快照、按名称限流和并发 |
| `HouseholdRelay/WindowsSecrets.cs`，全部 | 当前 Windows 身份 DPAPI、目录 ACL、加密文件原子替换 |
| `HouseholdRelay/ClientLauncher.cs:50`、`:70` | 地址/令牌变量外，还依赖私有 CA 及启动器；不能证明纯两字段接入 |
| `HouseholdRelay/DeviceMetadata.cs`，全部 | 只识别两种 metadata.user_id 设备格式，保留会话；不属于账户授权实现 |
| `HouseholdRelay.Tests/Program.cs:15`、`:286` | 10 项自定义测试，Fixture 默认 oauth，全部上游是本机模拟；没有 API 模式、网页、数据库、清理和用量测试 |

## 本轮实际运行

执行 `dotnet build HouseholdRelay.sln -c Release`，使用 .NET SDK 10.0.302，结果：构建成功，0 个警告，0 个错误。按 ClientLauncher 相同搜索顺序找到原生 Claude Code 后运行 `--version`，本机当前为 2.1.311；仓库旧 VALIDATION.md 记录的 2.1.300 不作为本轮版本证据。

随后执行 `dotnet run --project HouseholdRelay.Tests -c Release --no-build`，结果：10/10 passed，退出码 0。完整用例结果如下：

```text
PASS device normalization preserves sessions and prompt bytes
PASS host credentials/headers are shared; client sessions remain independent
PASS access control, endpoint whitelist and upload limits
PASS SSE flush and cancellation release upstream resources
PASS two clients run concurrently and queued revocation is enforced
PASS 429 cools down all clients without replay
PASS 401/403 pause upstream authentication
PASS timeouts release capacity and redirects never leak credentials
PASS DPAPI, TLS, independent client issuance and rotation
PASS real Claude Code clients through TLS and a TCP tunnel to mock upstream
10/10 passed; model traffic used only local mock servers.
```

最后一项启动真实 Claude Code 进程，但调用的是本机模拟上游。它证明客户端和现有中转协议在该场景可通，不能证明真实订阅令牌、真实 API 密钥、自定义 API 地址或公网入口可用。此处复跑基线，不是对新功能订单的验收。

## 官方资料核查方法

使用本机 `web-query` 技能的 `query_web.py` 抓取以下六页，全部返回 HTTP 200、`text/html; charset=utf-8`，最终 URL 与请求 URL 一致。正文直接存在 HTML 中；去掉 script/style 后读取了相关正文，不只依据标题或搜索片段。没有登录、没有复用浏览器 Cookie，也没有调用模型 API。

本机原始证据目录：`%TEMP%/HouseholdRelay-usability-review-20261028/`。各子目录含 `summary.json`、`page.html` 和元数据；原始页面是临时调查证据，下面保留可长期复查的来源、原文及推论。复核时应重新检查官方规则及协议是否变化。

| 子目录 | 官方标题及 URL | 本次核查重点 |
| --- | --- | --- |
| legal | [Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use) | 第三方网页登录及集中转发订阅凭据的边界 |
| authentication | [Authentication](https://code.claude.com/docs/en/authentication) | 官方登录、setup-token 的真实行为 |
| gateway | [Other LLM gateways](https://code.claude.com/docs/en/llm-gateway) | 服务端上游凭据和每开发者中转凭据的支持路径 |
| protocol | [Claude Code gateway compatibility guide](https://code.claude.com/docs/en/llm-gateway-protocol) | Messages、可选 Count Tokens、beta、SSE、错误、模型发现 |
| connect | [Connect Claude Code to an LLM gateway](https://code.claude.com/docs/en/llm-gateway-connect) | ANTHROPIC_BASE_URL 和 ANTHROPIC_AUTH_TOKEN 配置 |
| streaming | [Streaming messages](https://platform.claude.com/docs/en/build-with-claude/streaming) | usage 累计值、message_start/message_delta、ping/error 事件 |

### 账户授权的关键原文

Legal and compliance 正文：

> Anthropic does not permit third-party developers to offer Claude.ai login into their own applications, or to route requests through Free, Pro, or Max plan credentials on behalf of their users.

> Moreover, developers may not collect, store, or intermediate Claude.ai credentials or session tokens — sign-in to a Claude account must complete through Anthropic’s own flow.

同页又说明，客户为自己的授权使用者管理 API Key 或第三方推理提供商凭据，不因此被限制；让终端使用者在未经修改的官方 Claude Code 上以自己的订阅登录，也有另行说明的托管场景。

该资料说明不能宣称本项目已有受支持的第三方网页登录，也不能从 setup-token 可以生成令牌推导出真实请求必然成功。用户在本轮补充要求直接配置、以可调用为准，最终方案采用网页填写已有账户令牌与上游地址；不把官方支持或 OAuth 注册作为实施前置。资料核查与技术实测是不同证据，真正的连接结果仍需独立验证。

Authentication 正文确认：`claude setup-token` 会打开与 `/login` 相同的浏览器授权流程，批准后把一年期令牌打印到终端，命令不保存它；该令牌面向指定订阅方案的模型请求。仓库当前既未调用此命令完成网页授权，也没有客户端注册、PKCE/state、刷新或撤销管理。

### 网关与协议的关键原文

网关文档明确描述：provider key stays server-side，developers hold gateway credentials instead；管理员为每位使用者发凭据，通过凭据归属用量，撤销一个凭据完成退出。它与本方案的 API 路径一致。

兼容文档的 Anthropic Messages 行列出 `/v1/messages` 和可选 `/v1/messages/count_tokens`；要求保持 `anthropic-beta`、`anthropic-version`，并处理 `?beta=true`，不能只按完整 URL 匹配。`HEAD /api/hello` 是可拒绝的启动预热请求，仓库已有处理。

SSE 要逐事件到达，保留 ping 和正常终止事件。官方建议保留上游错误正文，供客户端识别能力拒绝并降级；因此“错误脱敏”不能简单地把所有错误都改成一个不可区分的文本。本项目要保留正常安全错误的语义，并对机密回显/非协议错误采用脱敏和安全替代，有相应用例证明取舍。

模型发现使用 `/v1/models?limit=1000`，Claude Code 的自动发现默认关闭，需要 `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`。因此“页面展示模型”与“客户端自动发现模型”是两项不同能力；不能额外要求所有普通使用者必须开启发现才能完成两字段调用。

Streaming messages 原文：

> The token counts shown in the usage field of the message_delta event are cumulative.

用量实现必须更新累计值，不能累加每个 message_delta 的 output_tokens。没有返回 usage、没有最终事件或中途断流，都不能推定消耗为零。
