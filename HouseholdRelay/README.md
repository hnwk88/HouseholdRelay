# Household Relay — C# 单账户中转

一个部署实例使用一个上游身份，多名使用者持各自的个人令牌。当前协议为 Anthropic Messages，客户端启动器支持 Claude Code；不提供多账户轮询。

当前版本已提供 **本机网页首次设置、管理登录、使用者和令牌管理、项目设置、SQLite 持久化与旧数据迁移**。账户/API 的网页直接配置、可信公网接入、个人用量与 40 天清理尚未提供，页面会显示真实状态。

## 启动管理页

需要 Windows 和 ASP.NET Core Runtime 10 x64（安装 .NET 10 SDK 也可以）。把发布程序放在可写目录后运行：

```powershell
.\HouseholdRelay.exe serve --home D:\RelayData
```

不带参数启动也会运行服务，默认数据目录是程序所在目录。新安装不要求安装 Claude Code，也不要求先填写上游凭据。

1. 在同一台电脑打开 `http://127.0.0.1:8788`。
2. 把启动窗口显示的本机引导码填入页面，设置至少 12 个字符的管理密码。
3. 登录后进入“使用者与令牌”，为每个人创建身份并复制其个人令牌。
4. 在“项目设置”填写对外地址和转发限制。保存地址只记录配置，不代表已建立公网连接；监听端口和转发限制需重启生效。

引导码仅在本机启动窗口显示，不放进网址。首次设置成功后锁定该入口。管理密码与个人令牌分别用于管理和调用，不能互换。管理页只监听 IPv4 回环地址，不能从远程、花生壳或代理访问；可用 `--admin-port 8788` 改管理端口。

个人令牌只在创建/轮换成功时显示一次，不回填列表、不存入浏览器存储，不默认导出所有人的客户端包。最多 8 名使用者，每人一个有效令牌。停用和轮换会立即阻止新请求，以及仍在排队或上传的请求；已发往上游的请求可以结束。轮换不会重新启用已停用的使用者。

## 数据与迁移

业务数据保存在 `--home` 下的 `.state/relay.db`，使用 SQLite、WAL、外键和短事务。数据库保存管理员密码哈希、使用者稳定 ID、令牌哈希/版本、配置及单一上游。上游凭据、证书私钥和管理 Cookie 密钥仍受当前 Windows 用户的 DPAPI 保护，`.state` 只允许该用户和 SYSTEM 访问。

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

`account` 继续支持本机终端隐藏输入上游凭据，并通过同一服务加密写入数据库，重启后使用。它不会验证实际模型调用，也不自动刷新凭据。`init --host 域名` 保留旧版私有 CA 配置能力，证书保存到数据库，不再探测 Claude Code 或自动创建三个人；已有证书时拒绝覆盖。新实例推荐先完成本机网页管理设置，后续版本再配置上游及对外连接。

已经迁移的 HTTPS 入口继续使用原证书、监听端口和上游。花生壳旧路径使用 TCP 透传，旧客户端仍需信任原私有 CA；这不是“只填地址和令牌”的可信 HTTPS 新路径。既有客户端包可继续运行 `client --bundle 文件夹`，其本机 Claude Code 安装及独立配置行为保持不变。

## 转发行为

- 只开放 POST `/v1/messages`、POST `/v1/messages/count_tokens`、GET `/v1/models`，以及 HEAD `/api/hello`。
- 个人令牌可通过 `Authorization: Bearer ...` 或 `x-api-key` 传入；同时传入不同令牌时拒绝。
- 默认全局并发 2、每人 1、最多排队 16、每人每分钟 120 次。限流和并发绑定不可变使用者 ID，改名或轮换不能重置它们。
- SSE 即时转发、取消释放资源；不自动重放，不跟随上游跳转。429 触发全局冷却，401/403 暂停上游，旧配置路径更新凭据后需重启。
- 上游仍固定为 `https://api.anthropic.com`，API 模式保留原有 `x-api-key` 转发；上游地址及两种凭据的网页直接配置由后续版本交付。
- 未配置上游时，已认证模型请求返回明确的 503。`/healthz` 只表示进程可达，带个人令牌的 `/status` 只提供运行状态，不是个人用量。
- 日志只记请求元数据；不记录请求/响应正文、密码、上游凭据或个人令牌。

## 开发与验证

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --self-contained false -o artifacts/HouseholdRelay-release
```

运行时新增 `Microsoft.Data.Sqlite 10.0.6`；Razor Pages、密码哈希、Cookie 和 CSRF 使用 ASP.NET Core 自带组件，不引入前端构建链。测试仍为现有自定义 .NET 测试程序；真实 Claude Code 用例使用本机模拟上游。浏览器测试单独使用 Playwright 和无窗口 Edge，复现命令及验收证据见 [管理与持久化验证](../docs/usability/FOUNDATION-VALIDATION.md)。

本次不包含真实账户/API 模型调用或公网验证，也未完成全部易用性需求。