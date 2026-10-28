# 账户/API 上游接入与 HTTPS：实施与验证

验证日期：2026-10-28，Windows，.NET SDK 10.0.302。实现了网页直接配置、候选测试和激活、按版本隔离的转发、可信 PFX 导入及统一连接说明。真实账户/API 凭据和公开可信 HTTPS 条件未提供，本轮未完成真实上游或公网实测；本地模拟结果不代表这些外部验收项通过。

## 实现选择

- SQLite schema v2 在事务中升级，原管理员、使用者 ID、个人令牌哈希和旧上游凭据保留。候选以不可变主键保存非秘密配置和 DPAPI 密文，检测结果关联候选，激活检查预期活动版本。网络检测期间不持有写事务。
- 管理员先保存候选，确认可能产生少量额度费用后检测，再显式激活。只有有效的 Anthropic Messages 回复才通过；401/403、429、跳转、错误协议、网络/TLS 失败或取消均不能激活。模型目录缺失单独显示，仍可按填写的模型进行实际调用。
- RelayServer 复用原白名单、个人鉴权、限流、排队和撤销检查；UpstreamPool 为请求保留完整配置及连接租约。旧请求返回的认证失败或冷却只影响旧版本，新请求使用同一快照内的地址、凭据及模式。
- 账户模式为 Bearer；Anthropic 预设添加 OAuth beta，兼容中转显式选择。API 为 x-api-key 或显式 Bearer，不附加 OAuth beta，不做账户设备归一。保留开放 beta、版本头、query、工具/思考/缓存字段、SSE ping 和结束事件。
- 地址合同：API 根地址可包含路径前缀和非认证查询参数；结尾 /v1 只保留一次。请求路径追加到前缀，配置 query 在前、下游 query 在后，重复参数不重写。禁止内嵌凭据、认证 query、片段和不明确的转义路径。页面显示最终 Messages/Models 地址。
- 默认 HTTPS，正常证书校验且不跳转；回环/RFC1918/IPv6 唯一本地地址需要明确允许，HTTP 只接受这些范围。DNS 返回的所有地址必须符合范围，随后按检查过的 IP 建连；HTTP CONNECT 代理也只能在本机/私网，CONNECT 目标为已检查 IP，保留原目标主机的 Host/TLS 名称。
- 普通 JSON 及完整 SSE 事件按解析后的字符串脱敏当前上游凭据及其 URL/base64 形式，覆盖 JSON 转义和跨网络分片。正常错误类型及能力拒绝语义保留；非 JSON 错误返回安全说明。响应头白名单保留请求标识、重试及 Anthropic 限流头，禁止 Cookie 和任意未知头。
- 可信入口选择直接 TLS 终止：网页导入 PFX，核验私钥、SAN 主机名、有效期、服务器用途、系统信任链和吊销状态；DPAPI 保存完整证书链，密码不保存。重启后加载，证书失效时仍保留本机管理入口。旧私有 CA 独立标记，并加载其配套链；新生成的旧模式证书带签发者密钥标识，避免多个同名私有 CA 导致链歧义。不安装系统根证书。
- “连接说明”提供实际验证的模型、根地址、Bearer/x-api-key 请求和 Claude Code 两个变量；已有个人令牌不回填。上游切换不改变使用者的地址和令牌合同。

## 已实际完成的验证

Release 构建 0 警告、0 错误；完整测试为 36/36 通过。原有 22 项仍全部保留，新增 14 项覆盖：

| 检查 | 实际证据 |
| --- | --- |
| URL、认证模式及协议 | 根 URL、/v1、前缀及 query；账户 Bearer 与 OAuth beta 开关、API x-api-key/Bearer；个人令牌未到上游，API 请求正文不被账户归一；Count Tokens、工具、签名和 cache 字段保留 |
| 候选和激活 | 未确认费用不发送诊断；有效协议调用后才能激活；伪 200 无效；两个候选竞争激活时旧版本被拒绝；设置重启后恢复 |
| 版本切换 | 将旧请求阻塞在真实上游，激活新上游，再让旧请求返回 401 或 429；新请求继续成功，旧请求未重放、没有新地址配旧密钥 |
| 诊断失败隔离 | 实际 401、403、429、307、500，不暂停当前活动版本、不跟随 Location；目录 404 后最小调用成功仍可激活 |
| 网络和 TLS | 错端口、HTTP 端口误写 HTTPS、不可信 TLS、DNS 后的私网范围拒绝、链路本地与映射 IPv6 拒绝、取消后不能激活；实际 HTTP CONNECT 转发及代理地址限制 |
| 秘密脱敏 | JSON 中 Unicode 转义的凭据、跨分片 SSE error、响应头及 Cookie；正常 invalid_request_error、thinking 能力拒绝、x-should-retry 和 request-id 仍保留 |
| 证书与存储 | 错误密码、不可信链、过期、域名不匹配、设置版本冲突均拒绝；测试独立根下导入成功、DPAPI 恢复完整链、实际 TLS 服务可达；未修改系统信任库 |
| 数据库升级反例 | 用旧版表结构和真实旧凭据，制造 DDL 名称冲突使升级中途失败；新增列和 schema 版本一起回滚，修复冲突后恰好升级一次，旧管理员、个人令牌及禁用状态保留 |
| 干净原生客户端 | Claude Code 2.1.311 在隔离配置目录，只提供根地址和个人令牌两个连接变量，未使用旧客户端包或 CA 文件，实际执行 Read 工具并返回结果；传输为本机 HTTP |

证书正例在测试函数内部使用独立 CustomRootTrust，测试根不写入系统；生产网页没有此入口，始终使用系统链检查。因此该测试证明导入/存储/TLS 实现，不冒充真实公网证书已被干净客户端直接信任。原有私有 CA/TCP 隧道客户端回归也仅作为旧路径回归。

完整回归曾发现同名私有 CA 的签名验证失败；仅独立复跑可通过。补充签发者密钥标识、配套证书链后，最终完整回归通过。前两次失败日志保留，未隐去该过程。

## 真实浏览器旅程

使用重新发布的产物、Edge 156.0.4314.50 和 Playwright 1.56.1，两个脚本均无窗口运行：

1. 原基础旅程重新通过首次设置、登录、创建甲乙、停用和轮换、网页与 CLI 并发修改、保存设置、重启恢复、窄屏及退出。
2. 新增旅程从全新目录开始，在网页填写 API 密钥与账户令牌，分别保存、确认测试、激活。通过真实 HTTP 完成 JSON/SSE 请求，验证前缀/query 和两模式认证差异。
3. 模拟账户凭据失效，第一次请求 401、随后暂停为 503，错误正文不泄露凭据；网页重新保存、测试和激活后，同一使用者令牌恢复调用。
4. 检查连接说明不会回填上游或个人令牌；网页导入无效 PFX 时明确报错且不启用 HTTPS；重启后活动上游及检测结果仍在，调用成功。
5. 查看 390px 的上游和连接说明页，没有水平溢出。新增七张截图已逐张目视检查；秘密输入在返回页面前清除，截图不含凭据。

完整本机输出和图片在 `artifacts/usability-upstream/`：

| 文件 | 内容 |
| --- | --- |
| build.log、tests.log | 最终构建与 36 项完整回归输出 |
| publish.log | 最终发布输出 |
| browser-foundation.log、browser-steps.md、01 至 06 PNG | 基础旅程重跑 |
| browser.log、upstream-browser-steps.md、07 至 13 PNG | 新增网页旅程、模式切换、失效恢复与窄屏 |
| tests-before-chain-fix.log、tests-chain-investigation.log | TLS 链调查时的失败输出 |
| mutation-build.log、mutation-tests.log、mutation-source/ | 独立副本移除原始/URL 凭据替换后的受控反例 |

## 复现与独立反例

工程根目录 PowerShell：

```powershell
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/usability-upstream/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/browser-journey.cjs artifacts/usability-upstream/publish/HouseholdRelay.dll artifacts/usability-upstream
node HouseholdRelay.Tests/upstream-browser.cjs artifacts/usability-upstream/publish/HouseholdRelay.dll artifacts/usability-upstream
```

缺少 Playwright 时按 [基础验证文档](FOUNDATION-VALIDATION.md) 安装；需要 Microsoft Edge。所有测试数据均为经根路径检查的独立临时目录。

可单独执行失败条件：

```powershell
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'upstream version switch'
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'upstream schema upgrade'
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'upstream JSON and SSE'
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'certificate validation'
```

本轮已实际建立独立源码副本，将 `UpstreamResponses.Redact` 中原始凭据和 URL 编码凭据两处匹配换为不匹配的夹具值，其他实现和测试保持一致。副本构建成功，`upstream JSON and SSE` 用例在“响应不能包含凭据”断言失败，结果 0/1、退出码 1；主工作区的相同用例通过。总监可从本次提交导出独立副本，重复这两个替换后运行相同过滤词。副本只供反例验证，不能用于部署。

## 未实测项与能力边界

未收到真实账户/API 上游、公开域名、可信证书或现有公网入口条件。本轮没有真实费用调用，没有验证真实账户令牌更新、真实 API/账户切换后的公网调用，也没有通过干净客户端在系统直接信任的公开 HTTPS 上只配置两项完成调用。以上仍须在管理员网页录入真实凭据和证书后补做，不能据此宣布整体验收通过。

本期选择证书导入和直接 TLS，不增加受信反代头配置。管理入口仍为本机回环；证书、监听端口及转发限制变更需重启。每版本的暂停/冷却为进程内状态，重启会清空。出站代理仅支持无用户名密码的本机/私网 HTTP CONNECT。

成功 JSON 响应上限 16 MB，错误及单个 SSE 事件上限 1 MB，诊断总超时 30 秒；上游需接受 identity 编码。普通请求继续遵循设置的超时，流式不缓存完整回答。仅支持 Anthropic Messages，不做 OAuth 登录/刷新、多账户调度、其他协议转换或个人用量。未部署、未推送，后续任务仍按验收顺序推进。
