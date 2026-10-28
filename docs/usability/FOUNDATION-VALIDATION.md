# 管理与持久化基础：实施与验证

验证日期：2026-10-28，Windows，.NET SDK 10.0.302。此交付完成本机管理、配置/身份持久化及旧数据迁移。真实上游调用、网页上游配置、可信公网接入和个人用量均不在本次交付范围。

## 实现选择

- 使用 ASP.NET Core Razor Pages，同程序发布静态 CSS/JS，无前端构建链。默认 127.0.0.1:8788 管理入口独立于旧 HTTPS 转发入口；无上游、无使用者也可以启动。
- RelayDatabase 管理 schema v1、实例标记及连接。IdentityService、AdministratorService、ConfigurationService 由网页和 CLI 共同调用。Microsoft.Data.Sqlite 固定为 10.0.6。
- SQLite 使用 WAL、FULL 同步、每连接外键、10 秒忙等待、非池化短连接与立即写事务；启动/迁移以 Windows 实例互斥量协调。转发网络等待不持有数据库事务。
- 使用者及令牌版本均有独立稳定主键。用户总数上限 8，令牌历史不占名额，停用状态不会因轮换变为启用。限流/排队按使用者 ID 分配。
- 写事务内构建并验证权限快照，提交后发布；任何失败不发布半成品。鉴权时再读取当前数据库快照，覆盖另一 CLI 进程的已提交撤销。入队前、出队后、上传后和发出前都检查令牌；已发出的请求允许完成。
- 管理密码使用标准 Identity PasswordHasher（PBKDF2，210,000 次迭代）。管理 Cookie 受 DPAPI 保护的 Data Protection 密钥签发，HttpOnly、SameSite=Strict、8 小时有效；本机 HTTP 入口使用 SameAsRequest，若入口是 HTTPS 则标 Secure。退出同时删除服务端会话，复制的旧 Cookie 也失效。
- 精确校验回环来源、Host 与端口、写表单 Origin，拒绝 cross-site；Razor 自带 CSRF 验证。登录及首次设置分别限制为每分钟 5 次 POST。Referrer-Policy 使用 same-origin，避免 Chromium 的 Origin:null 表单问题。
- .relay-instance 放在 .state 外，用于识别数据库/数据目录意外遗失。首次设置需要仅在本机启动窗口显示的随机引导码；成功后事务性移除引导凭据。存储打不开时返回 503，启动也不会重建空库。

## 迁移和恢复

仅访问 --home 指定目录内的本项目旧文件。启动前应先停止旧服务；新服务读取 server.json、account.dpapi、tls.dpapi、relay-ca.pem，把同一批字节复制到受 ACL 保护的 legacy-backups，随后在单事务中导入。

旧 TokenHash、启停状态、DeviceId、User-Agent、DPAPI 凭据与证书保持原值。旧名称映射到新使用者主键，初始令牌版本关联同一主键；旧客户端仍可用原令牌。导入成功记录完成标记，不再读旧 JSON，修改旧文件不会覆盖新权限。

失败时原文件不改，数据库导入事务整体回滚，下次启动可重新导入。已初始化实例的数据库/标记遗失不能被当成新实例。恢复应停止服务，恢复完整 .state 和配套 .relay-instance；不提供越过实例检查的自动重置。跨 Windows 身份恢复 DPAPI 凭据不在本次能力范围。

## 实际检查与证据

完整本机输出及脱敏截图在 artifacts/usability-foundation/（生成产物，不提交仓库）：

| 文件 | 内容 |
| --- | --- |
| build.log | 最终 Release 构建输出，0 警告、0 错误 |
| tests.log | .NET 测试完整输出，17/17 通过 |
| publish.log | 发布产物生成结果 |
| browser.log | 发布产物的真实无窗口 Edge 旅程结果 |
| browser-steps.md | 浏览器版本与完成的操作记录 |
| 01-first-setup.png | 干净目录首次设置页 |
| 02-overview.png | 管理概览，未接入能力如实标记 |
| 03-issued-token-redacted.png | 新令牌一次性展示，截图已遮盖明文 |
| 04-users.png | 两人状态及管理操作 |
| 05-settings.png | 设置保存及重启生效说明 |
| 06-mobile-after-restart.png | 重启后 390px 窄屏页面，无水平溢出 |

原有十项测试全部保留。原存储测试因“不再自动导出三份明文客户端包”的新要求，改为通过同一身份服务显式创建三名使用者并检查不会生成默认包；DPAPI、TLS、独立发行及轮换仍有验证。其余原转发、SSE、并发、取消、冷却、认证暂停、跳转和真实 Claude Code 用例保留。

新增七项覆盖：重启持久化与会话撤销；旧数据迁移；迁移故障/数据库遗失；并发写入和人数上限；失败权限写入不半生效；网页安全边界；数据库驱动的排队和上传撤销。所有模型流量只发往本机模拟上游。

## 复现命令

在工程根目录 PowerShell 执行：

    dotnet build HouseholdRelay.sln -c Release
    dotnet run --project HouseholdRelay.Tests -c Release --no-build
    dotnet publish HouseholdRelay -c Release --self-contained false -o artifacts/usability-foundation/publish
    npm.cmd install --prefix "$env:TEMP\HouseholdRelay-browser-tools" playwright@1.56.1 --no-audit --no-fund
    $env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
    node HouseholdRelay.Tests/browser-journey.cjs artifacts/usability-foundation/publish/HouseholdRelay.dll artifacts/usability-foundation

浏览器脚本需要已安装 Microsoft Edge，使用临时目录、虚构密码和随机临时令牌，无窗口运行，结束时只清理经过根路径检查的本次临时目录。截图不包含明文凭据。测试服务器使用随机回环端口，不启动正式部署，也不修改已有 .state。

手工浏览器路径：启动 serve --home 新目录 → 在本机管理页输入启动窗口引导码 → 设置管理密码 → 登录 → 创建甲、乙 → 停用乙 → 轮换甲 → 保存项目设置 → 停止并重启 → 再登录确认列表、设置和令牌状态 → 退出后访问列表必须再次登录。

## 可独立检查的反例

故障路径可直接复现：

    dotnet run --project HouseholdRelay.Tests -c Release --no-build -- "migration rollback"
    dotnet run --project HouseholdRelay.Tests -c Release --no-build -- "failed permission writes"
    dotnet run --project HouseholdRelay.Tests -c Release --no-build -- "queue and upload boundaries"

第一项在真实旧格式夹具全部插入后、迁移提交前注入 IOException，检查原文件未变，再次启动恰好导入一次；随后删除本次临时数据库，确认有实例标记时不会重建。第二项给临时库安装拒绝 INSERT 的 SQLite 触发器，让轮换在撤销旧版本之后失败，验证事务回滚后旧令牌仍可用，再移除触发器验证正常轮换。第三项用真实 HTTP 把请求挡在排队和分段上传处，由另一个数据库服务提交轮换/停用，释放请求后必须是 401，模拟上游调用次数不得增加。这些故障只作用于测试独有的临时数据。

总监若要做受控代码反例，可在独立临时检出中去掉 LegacyImport.Run 末尾的 transaction.Commit()，重新构建并运行迁移用例；预期旧令牌继续可用、用户恰好导入等断言失败。不要在真实数据目录执行故障操作。

## 已知边界

- 管理入口仅支持本机 IPv4 回环 HTTP；远程管理及可信 HTTPS 由后续工作处理。
- 已迁移的上游/证书继续使用；网页只能显示凭据配置状态，不能宣称模型调用已成功。新实例无上游时模型请求明确返回 503。
- 配置端口、转发限制和 CLI 上游凭据修改需重启；使用者权限立即生效。设置使用版本检查，发生冲突需重新加载页面。
- 每人一个有效令牌、最多 8 名使用者；不提供删除使用者、密码重置或跨 Windows 身份恢复工具。
- 本次不采集或展示个人用量，也未实现 40 天清理；不存在用零填充未知用量的页面。
- 仅验证真实浏览器、本机 HTTP/HTTPS、SQLite 和模拟上游。没有真实账户/API 凭据或公网联调，不据此宣布完整需求完成。
