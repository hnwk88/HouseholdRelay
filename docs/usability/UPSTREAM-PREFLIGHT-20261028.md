# 真实中转接入预检与 MyCoAI 参考

日期：2026-10-28。角色：总监。本次是正式验收前的接入预检，没有改变订单状态，没有修改业务代码。

## 结论

用户提供的两个中转凭据均取得了真实调用成功证据，不能再将“没有真实 API 上游材料”作为未验证原因。按用户提示和 MyCoAI 获取模型的判断顺序，第一家配置为 API 模式下的 Bearer，第二家配置为 API 模式下的 x-api-key；它们都不等同于真实账户 OAuth 接入证据。

- `https://api.sftoken.top`：上游认证采用 x-api-key。在独立临时数据目录运行订单 61 的发布程序，通过管理页面完成候选保存、测试和激活。随后使用本项目生成的个人令牌，经 HouseholdRelay 调用 `claude-opus-5-5`，非流式 JSON 和 SSE 均为 HTTP 200；SSE 包含 `message_stop`。没有将上游凭据交给下游请求。
- `https://aitoll.net/v1/cc`：Bearer 模型目录返回 HTTP 200。隔离的原生 Claude Code 2.1.311 使用该地址、用户提供的凭据和 `claude-opus-5-5`，成功返回 `OK`。但 HouseholdRelay 管理页最小调用返回 HTTP 400；同样的简短 Messages 请求直接发给该上游也返回 `client_request_rejected`。目前确认的是实际客户端调用与最小探测请求存在兼容性差异，尚未确定具体哪个请求字段导致拒绝；不能把它写成令牌无效，也不能写成已通过本项目转发。

两者均沿用 MyCoAI 当前启用的系统代理，本次解析到的本机代理为 `http://127.0.0.1:7897`。HouseholdRelay 的“出站 HTTP CONNECT 代理”须独立填写；上游根地址仍填写用户给出的 HTTPS 地址。

## 预检条件与失败记录

- 使用工程师交付的 `artifacts/usability-upstream/publish/HouseholdRelay.exe`。管理端和下游请求均在本机回环 HTTP 上运行，HTTPS 出站保持证书校验，不跟随重定向。
- 第一家的最终请求路径为 `/v1/cc/v1/models` 和 `/v1/cc/v1/messages`，保留用户提供的前缀。第一家使用 `x-api-key` 获取目录时返回 401，改为 Bearer 后目录成功。
- 最初没有使用系统代理，第二家目录请求返回 503，页面标题为“该网页在当前地区不可用”。经系统代理请求同一目录返回 200。这一现象不能用来判定凭据无效。
- 最初选取 `claude-haiku-4-5`，第二家返回 `model_not_found`。按用户明确给出的 `claude-opus-5-5` 复测后成功；模型是否列在另一家目录里不能代替本家的实际可调用证据。
- 第二家先前使用 Bearer 的代理请求也曾成功，但最终按 MyCoAI 优先探测并确认的 x-api-key 独立重跑了目录、网页检测、激活和 JSON/SSE 转发，全部成功。最终配置不将两个上游统一写成 Bearer。
- 第二家成功的非流式请求与流式请求各报告输入 16、输出 5 个 Token。网页诊断另有一次最小调用。只发送了预检用的简短文本，没有发送工程文件或用户业务内容。
- 第一家的原生客户端成功请求报告输入 2、缓存创建 1619、输出 4 个 Token。客户端显示的 `0.008183 USD` 是其模型价格估算，不是中转平台实际账单。
- 临时服务在预检结束后均已停止。证据只保存状态、模型、用量、必要错误与配置说明，不记录令牌、管理密码或 Cookie。
- 结束后的临时目录删除操作被自动审批策略拦截，未给出进一步原因。目录暂留本机；其中本项目保存的上游凭据由 DPAPI 保护，未将其写入仓库或本报告。没有换用其他删除方式绕过拦截。

## MyCoAI 可参考的现成实现

参考工程：`E:/VisualStudio/source/repos/MyCoAI`。本次只读取源码、相关测试与必要的非秘密代理设置，没有切换或修改 MyCoAI 的账户。

已核对用户提到的近期修复：`3f3f3a27ed05f0229d9eb94521bd9cfcee86cb67`，提交于 2026-10-26 21:54:30，标题为“第三方 Claude 账户认证方式改为自动判定：x-api-key 被拒改试 Bearer 并存档”。修改覆盖目录探测、界面回填、账户保存、持久化和客户端启动，不能只取其中一次换请求头的行为。

1. `src/Infrastructure/Auth/AccountModelCatalogClient.cs`：Claude 模型目录先试 `x-api-key`，仅在 400/401/403 时有限地改试 Bearer；保存已确认的认证方式。非认证失败不盲目换头重试。`BuildModelsUri` 保留路径前缀并避免重复追加末尾 `/v1`；网络请求遵循统一代理设置。
2. `src/Infrastructure/Auth/EnvironmentVariableClaudeCodeProcessLauncher.cs`：Bearer API 凭据使用 `ANTHROPIC_AUTH_TOKEN`，同时移除继承的 `ANTHROPIC_API_KEY`；地址与凭据来自同一配置快照。API 使用 Bearer 与官方账户登录是两个概念。
3. `tests/Tests/Auth/AccountModelCatalogClientTests.cs`：已有针对 aitoll 这类只接受 Bearer 的服务的用例，覆盖认证回退、失败次数限制和响应正文不泄露。
4. `tests/Tests/Auth/EnvironmentVariableClaudeCodeProcessLauncherTests.cs`：已有 `/v1/cc` 地址、Bearer 环境变量和账户切换后新进程读取新配置的用例。
5. `docs/KnowledgeBase/MyCoAI/053-account-switching-encrypted-field-snapshots.md`：说明模型目录只是远端缓存，实际生成才证明可调用；凭据用 DPAPI 保护，切换仅更新所属配置字段。
6. `src/MyCoAI/Modules/Auth/AddApiAccountViewModel.cs`：获取模型后使用 `catalog.AuthenticationScheme` 回填“认证方式”组合框；保存时显式带上当前选项。用户提示的“Bearer 令牌（Authorization 头）”对应这一条链路。
7. `src/Infrastructure/Auth/AccountManagementService.cs`：保存账户时按已经确认的认证方式查询一次目录，并将该方式随地址、令牌存入同一账户快照。模型目录成功后，正常生成由真实 Claude Code 完成；这里没有 HouseholdRelay 当前使用的固定 16 Token 最小 Messages 激活检测。

本项目已提供手动选择 API 认证头、前缀保留和独立代理字段，因此上述参考不意味着必须重新实现这些能力。后续验收应重点核对第一家的最小检测与实际客户端请求差异；修正应有真实请求证据，不能靠伪造检测成功或绕过候选激活条件解决。MyCoAI 的模型目录自动判断可供易用性改进参考，不能未经判定扩展成生成请求的自动重放。

## 证据与尚未覆盖的范围

脱敏证据位于 `artifacts/upstream-compatibility-20261028/`：

- `discovery.json`、`sftoken-bearer.json`：初始目录请求及认证结果。
- `relay-preflight.json`、`direct-diagnostic.json`：最初直连与 Haiku 模型的失败记录。
- `relay-opus-proxy-preflight.json`：沿用系统代理、使用用户模型后的管理页面预检和第二家 JSON/SSE 转发成功证据。
- `aitoll-native-client.json`：第一家实际 Claude Code 成功调用的客户端版本、状态及用量。
- `sftoken-api-key-models.json`、`sftoken-api-key-relay.json`：第二家使用 x-api-key 的目录和完整本机转发复测证据，是最终推荐配置的依据。

本次不构成订单 61 的完整验收：没有验证自建公网入口的可信 HTTPS，没有完成真实账户令牌更新，也没有完成第一家经过 HouseholdRelay 的客户端调用。第二家成功只证明本次配置、模型和调用场景，不扩写为所有模型与所有客户端功能均已通过。
