# Household Relay — C# 单账户中转

一个部署实例使用一个上游身份，多名使用者持各自的个人令牌。当前协议为 Anthropic Messages，客户端启动器支持 Claude Code；不提供多账户轮询。

当前版本已提供 **本机网页管理、SQLite 持久化、个人令牌、上游候选测试与激活、网页私有 CA、客户端公共信任包、证书导入、个人用量与金额及滚动 40 天清理**。当前私人部署使用真实 API，每台 Windows 电脑安装一次信任，日常只配置中转地址和个人令牌。完整步骤见[私人部署与恢复](../docs/usability/PRIVATE-DEPLOYMENT.md)。

## 启动管理页

需要 Windows 和 ASP.NET Core Runtime 10 x64（安装 .NET 10 SDK 也可以）。把发布程序放在可写目录后运行：

```powershell
.\HouseholdRelay.exe serve --home D:\RelayData
```

不带参数启动也会运行服务，默认数据目录是程序所在目录。新安装不要求安装 Claude Code，也不要求先填写上游凭据。

1. 在同一台电脑打开 `http://127.0.0.1:8788`。
2. 把启动窗口显示的本机引导码填入页面，设置至少 12 个字符的管理密码。
3. 进入“上游接入”，选择账户令牌或 API 密钥、地址、认证方式和模型，填写凭据并保存候选；确认可能产生少量额度消耗后测试，通过最小调用后激活。
4. 进入“HTTPS 入口 → 私人设备”，填写客户端可达的 HTTPS 根地址和本机监听端口，创建私有 CA 与服务器证书。重启服务；本机试用可用 `https://localhost:8787`，其他电脑需使用主机的局域网地址或可解析名称。
5. 在“连接说明”下载公共信任包，逐台核对 CA 指纹并安装当前用户系统信任及 Claude Code 额外 CA。Windows 可能要求确认根证书；完成后完全退出并重新打开终端和客户端。
6. 进入“使用者与令牌”为每个人生成令牌，在“连接说明”复制中转根地址和示例，选择实际检测过的模型。先检查 HTTPS `/healthz`，再验证 Claude Code 实际调用。
7. “项目设置”可调整转发限制；监听端口、证书及转发限制需重启，网页激活上游和个人权限立即生效。

引导码仅在本机启动窗口显示，不放进网址。首次设置成功后锁定该入口。管理密码与个人令牌分别用于管理和调用，不能互换。管理页只监听 IPv4 回环地址，不能从远程、花生壳或代理访问；可用 `--admin-port 8788` 改管理端口。

个人令牌只在创建/轮换成功时显示一次，不回填列表、不存入浏览器存储，不默认导出所有人的客户端包。最多 8 名使用者，每人一个有效令牌。停用和轮换会立即阻止新请求，以及仍在排队或上传的请求；已发往上游的请求可以结束。轮换不会重新启用已停用的使用者。

## 数据与迁移

管理员在“用量”页面按使用者、时间范围、接口类别、模型及结果筛选，可按天或小时汇总。个人打开中转根地址下的 `/my-usage`，输入自己的个人令牌查询；令牌不进入网址或浏览器存储。改名和轮换保留历史归属，旧令牌及停用令牌不能查询。

每条记录显示调用类型、模型、输入/输出及缓存 Token、结果、发送状态和金额。Token 采用上游实际报告；累计 SSE 用量只更新，不反复相加。缺少或中断的用量保留已知部分并标明不完整，全部未知显示“未知”。Count Tokens 是估算请求，Models 不计 Token；管理检测、健康检查和 HEAD 预热不计入个人消耗。

金额优先使用明确返回的上游费用；没有费用时，可在“用量 → 配置模型估算费率”填写当前上游版本与模型每百万 Token 的单价及币种。未配置时金额未知，估算始终标明来源，不等同于实际账单；不同币种和来源分开汇总。未知单价留空，明确免费才填 0。价格在发送时保存快照，改价不影响历史；重新激活上游后须重新核对费率。支持的返回费用字段、单位和实际结果见[用量验证](../docs/usability/USAGE-VALIDATION.md)。

记录按 UTC 请求开始时间保留 40 × 24 小时，严格早于截止点才删除；恰在边界保留。启动及每小时按 500 条一批清理，查询始终过滤窗口。显示和小时/天分组使用时区，但不会按本地午夜删除。配置、使用者、令牌和费率不随用量清理。

起始记录或发送标记保存失败时，服务拒绝发送请求。结束保存不受客户端取消影响，最多尝试三次；失败会在用量页面提示并写入 `usage_storage_failure` 元数据日志。启动恢复遗留请求为“中断 / 不完整”，不能据此推定零消耗。同一实例同时只允许一个服务运行，命令行管理仍可并发使用。

业务数据保存在 `--home` 下的 `.state/relay.db`，使用 SQLite、WAL、外键和短事务。数据库保存管理员密码哈希、使用者稳定 ID、令牌哈希/版本、配置、单一上游、逐请求用量和费率；用量表不保存提示词、回答或凭据。上游凭据、证书私钥和管理 Cookie 密钥仍受当前 Windows 用户的 DPAPI 保护，`.state` 只允许该用户和 SYSTEM 访问。

旧版本升级前停止旧服务，再用新程序指定同一个 `--home`。启动时自动迁移本项目的 `.state/server.json`、`account.dpapi`、`tls.dpapi` 和 `relay-ca.pem`：

- 原始文件先复制到 `.state/legacy-backups/`，再用事务导入数据库；导入失败不修改原文件。
- 原有个人令牌、启停状态、设备 ID、User-Agent、凭据与证书保留；旧客户端名成为默认显示名，并生成稳定使用者 ID。
- 旧实例还需在本机网页创建管理密码；这不会改变已迁移的个人令牌。
- 成功迁移只执行一次。此后 JSON 是旧数据备份，不再轮询，不再参与配置更新；不要继续编辑它管理新版本。
- 仅迁移管理员指定的本项目目录，不读取官方 Claude 登录文件。

备份时停止服务，复制完整 `.state` 和同目录的 `.relay-instance`，恢复时保持配套。**不要只复制正在运行中的 relay.db**，WAL 中可能还有已提交数据。数据库或实例标记遗失、损坏、权限异常时拒绝启动，不会自动开放重新设置；请恢复备份。DPAPI 数据换 Windows 身份后不能保证解密，本版本不提供网页重置或跨身份恢复工具。

## 保留的命令行入口

网页和命令行调用同一数据库服务，可以并发管理，令牌撤销无需等待文件轮询。建议用列表中的主键指定使用者：

```powershell
.\HouseholdRelay.exe clients list --home D:\RelayData
.\HouseholdRelay.exe clients add --display-name 小王 --home D:\RelayData
.\HouseholdRelay.exe clients disable --id 使用者主键 --home D:\RelayData
.\HouseholdRelay.exe clients enable --id 使用者主键 --home D:\RelayData
.\HouseholdRelay.exe clients rotate --id 使用者主键 --home D:\RelayData
.\HouseholdRelay.exe clients rename --id 使用者主键 --display-name 新显示名 --home D:\RelayData
```

为兼容旧操作，可用 `--name office` 查找原客户端。CLI 创建/轮换仅在终端输出本次新令牌；请妥善保管终端记录，不重定向到共享日志。不会再写入含明文令牌的默认 `clients/` 目录。

`account` 继续支持本机终端隐藏输入上游凭据，并通过同一服务加密保存为候选；不会直接覆盖活动上游。请在网页补齐模型及完整配置、重新填写秘密、测试后激活。它不自动刷新凭据。`init --host 域名` 保留旧版私有 CA 配置能力，证书保存到数据库，不探测 Claude Code 或自动创建使用者；已有证书时拒绝覆盖。

已经迁移的 HTTPS 入口继续使用原证书、监听端口和上游，可在网页下载原 CA 的公共信任包。TCP 映射应透传到本机 HTTPS 端口；映射、DNS 与证书信任分别配置。既有客户端包可继续运行 `client --bundle 文件夹`。

## 转发行为

- 只开放 POST `/v1/messages`、POST `/v1/messages/count_tokens`、GET `/v1/models`，以及 HEAD `/api/hello`。
- 个人令牌可通过 `Authorization: Bearer ...` 或 `x-api-key` 传入；同时传入不同令牌时拒绝。
- 默认全局并发 2、每人 1、最多排队 16、每人每分钟 120 次。限流和并发绑定不可变使用者 ID，改名或轮换不能重置它们。
- SSE 按完整事件即时刷新、取消释放资源；不自动重放，不跟随上游跳转。429 冷却和 401/403 暂停归属对应上游版本；网页测试并激活新凭据后恢复，旧请求的迟到错误不污染新版本。
- 账户模式使用 Bearer，Anthropic 预设添加 OAuth beta；兼容中转按明确选项选择。API 默认 x-api-key，也可显式选择 Bearer，不携带 OAuth beta、不改账户设备元数据。
- API 模式保留客户端的有效 User-Agent；账户模式仍使用部署设置中的统一 User-Agent。这样可保留上游要求的原生客户端请求特征。
- 根 URL、结尾 /v1 和自定义路径前缀均可配置；前缀保留，/v1 只添加一次。非认证 query 保留，下游 query 随后追加；相同参数允许重复，按原顺序传给上游，不猜测覆盖语义。配置地址禁止内嵌凭据及认证 query。
- 默认 HTTPS 并验证证书。回环/RFC1918/IPv6 唯一本地地址须显式允许，HTTP 只接受这些范围；DNS 解析后检查并按验证 IP 建连。可选本机/私网 HTTP CONNECT 代理与 API 地址分开，代理 URL 不支持用户名密码或 HTTPS。
- 响应头只返回必要 request-id、retry-after、x-should-retry 和 Anthropic 限流头；不返回 Cookie 或未知敏感头。JSON 和 SSE 事件内替换当前上游凭据及其 URL/base64 表示，保留正常错误类型和消息的其余内容；非 JSON 错误换为安全错误说明。
- 未配置上游时，已认证模型请求返回明确的 503。`/healthz` 只表示进程可达，带个人令牌的 `/status` 只提供运行状态，不是个人用量。
- 日志只记请求元数据；不记录请求/响应正文、密码、上游凭据或个人令牌。

## 上游与证书的使用边界

只有有效 Anthropic Messages 回复才算最小调用成功。连通、模型目录返回或单独 HTTP 200 都不能通过激活检查；缺少模型目录时可以填写模型并完成实际调用。诊断不改变活动版本的暂停/冷却，不计入个人业务流量。上游密钥不回填，过期时间未知，不自动续期。两个浏览器同时更改活动配置时，旧候选激活会提示冲突。

默认“标准 Messages”检测不需要安装 Claude Code。部分兼容中转只接受原生客户端请求：如果同一配置在 Claude Code 可用，但标准检测返回 400，可显式选择“Claude Code 实际调用”，重新保存候选后测试。这一选项需要服务所在电脑安装原生 Claude Code；它在独立目录禁用工具进行一次真实调用，上游凭据留在中转进程内，出站仍使用候选配置的代理及 TLS 校验。检测失败不会自动改认证或重放生成请求。通过只证明 Claude Code 场景，连接说明会提示普通 curl 请求可能仍被上游拒绝。

已实测 `https://aitoll.net/v1/cc` 使用 API/Bearer/Claude Code 检测，`https://api.sftoken.top` 使用 API/x-api-key/标准检测，模型均为 `claude-opus-5-5`。两家均完成网页接入、JSON/SSE、原生工具会话、切换及重启后调用；适用范围及复现见[兼容性整改验证](../docs/usability/UPSTREAM-REWORK-VALIDATION.md)。

私有 CA 证书验证名称 SAN、有效期、服务器用途及本实例的确切信任根。CA 私钥只在签发期间存在内存；服务器私钥用 DPAPI 保存。证书或地址更新后重启生效，重新创建会生成新 CA，每台客户端必须重新核验并安装。公共信任包只有 CA 公共证书、安装脚本与说明。

以后采用公网可信 CA 时，可导入 PFX：验证私钥、域名 SAN、有效期、服务器用途、系统证书链与吊销状态；密码不保存。证书无法验证时保留本机管理入口并拒绝启用 HTTPS。两种方式均保持正常 TLS 校验。本期由程序直接终止 TLS，不提供管理反向代理配置或受信代理头入口；保存地址不会自动配置域名、防火墙或公网映射。

JSON 成功响应上限 16 MB，错误和单个 SSE 事件上限 1 MB，诊断总超时 30 秒；不缓存整条流。上游需接受 `Accept-Encoding: identity`。暂停和冷却为进程内状态，重启清空；活动配置、成功诊断和已结束用量持久化。启动时会将 schema v1/v2 事务迁移到 v3；升级前按上面的完整备份步骤保留副本，旧程序不能读取 v3。

## 开发与验证

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --self-contained false -o artifacts/HouseholdRelay-release
```

运行时使用 `Microsoft.Data.Sqlite 10.0.6`；Razor Pages、密码哈希、Cookie 和 CSRF 使用 ASP.NET Core 自带组件，不引入前端构建链。测试为自定义 .NET 测试程序；真实 Claude Code 用例使用本机模拟上游。浏览器测试使用 Playwright 和无窗口 Edge，证据见 [管理与持久化验证](../docs/usability/FOUNDATION-VALIDATION.md) 与 [上游接入验证](../docs/usability/UPSTREAM-VALIDATION.md)。

用量实现已有 54 项基线回归；私人部署新增证书与信任验证，最新实际结果、失败记录和未执行项见[私人部署验证](../docs/usability/PRIVATE-DEPLOYMENT-VALIDATION.md)。真实账户与公网部署仍是用户试运行后按需安排的事项。
