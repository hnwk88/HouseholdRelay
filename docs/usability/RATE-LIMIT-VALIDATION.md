# 管理表单限速：整改与验证

验证日期：2026-10-28，Windows，.NET SDK 10.0.302。修复登录与首次设置通过有效路径别名绕过每分钟 5 次 POST 限制的问题。

## 修复方式

原实现用原始 Request.Path 识别 /Login 和 /Setup；Razor Pages 同时接受带尾斜杠的路径，因此别名执行同一个处理器却进入无限制分区。

ManagementWeb 在 UseRouting 之后读取已匹配端点的 PageActionDescriptor.ViewEnginePath，以框架确定的规范页面身份选取固定窗口限速器。登录与首次设置各有每分钟 5 次的预算；同一页面的大小写、尾斜杠及查询参数形式共享预算。无需自行维护一份 URL 规范化规则。

超过预算时仍返回 429 与 Retry-After: 60，GET 不计入 POST 预算。限速在表单处理器执行前完成，因此正确凭据也不能在耗尽后创建管理员、消耗引导凭据或签发管理会话。

## 实际 HTTP 回归与修复前后对比

新增 WebRateLimitTests.cs，复用真实 Kestrel、有效 Origin/CSRF 和独立临时 SQLite。测试先验证各别名可打开表单，前五次错误凭据确实到达凭据检查，再用另一 Cookie 会话提交正确凭据。每次拒绝都同时检查状态码和数据库中的管理员、管理会话、引导凭据数量，以及未签发管理 Cookie、仍不能访问受保护管理页。

| 用例 | 修复前 | 修复后 |
| --- | --- | --- |
| 标准 /Setup 五次后提交 /Setup/ | 302；管理员由 0 变 1，引导凭据被消耗 | 429；管理员 0、会话 0、引导凭据 1 |
| 标准 /Login 五次后提交 /Login/ | 302；会话由 0 变 1 | 429；管理员 1、会话 0 |
| 首次设置交替路径累计五次，再提交正确凭据 | 302；管理员被创建 | 全部别名返回 429，无写入副作用 |
| 登录交替路径累计五次，再提交正确凭据 | 302；管理会话被创建 | 全部别名返回 429，无写入副作用 |
| 各页面第五次提交正确凭据 | 通过 | 通过；设置和登录预算独立 |

别名矩阵包括标准路径、尾斜杠、全小写、全大写加尾斜杠、尾斜杠加查询参数、标准路径加查询参数。上述请求在各自同一分钟窗口内完成。第五次合法尝试的正例还验证登录后能够访问 /Users。

先在原业务实现上执行新增用例，结果 1/5 通过、4 项失败，退出码 1；失败指向绕过后真实产生的管理员或会话。随后修改业务实现，以相同用例复跑，5/5 通过，退出码 0。

## 完整验证与证据

本机完整输出和脱敏截图位于 artifacts/usability-foundation/rate-limit-rework/，生成产物不提交仓库。

| 文件 | 结果 |
| --- | --- |
| before-build.log | 原业务实现加新增回归，Release 构建成功，0 警告、0 错误 |
| before-tests.log | 修复前完整 HTTP 输出，4 项回归失败、1 项正例通过，退出码 1 |
| build.log | 修复后 Release 构建成功，0 警告、0 错误 |
| after-tests.log | 修复后相同 5 项用例全部通过，退出码 0 |
| tests.log | 原有 17 项加新增 5 项，共 22/22 通过，退出码 0 |
| publish.log | 修复后的发布产物生成成功 |
| browser.log、browser-steps.md | Edge 156.0.4314.50 完整无窗口浏览器旅程通过，退出码 0 |
| 01-first-setup.png 至 06-mobile-after-restart.png | 六张截图已目视检查，含 390px 窄屏；一次性令牌已遮盖 |

浏览器使用本轮发布产物和全新临时数据目录，完成首次设置、登录、甲乙创建、一次性令牌展示、停用与轮换、网页与独立 CLI 并发管理、保存配置、停止和重启、再次登录恢复、窄屏及退出验证。没有页面脚本错误或水平溢出。

在工程根目录 PowerShell 复跑：

```powershell
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'web rate limit'
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/usability-foundation/rate-limit-rework/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/browser-journey.cjs artifacts/usability-foundation/rate-limit-rework/publish/HouseholdRelay.dll artifacts/usability-foundation/rate-limit-rework
```

浏览器脚本使用 Microsoft Edge 和 Playwright 1.56.1；缺少工具时按 [基础验证文档](FOUNDATION-VALIDATION.md) 安装。所有服务仅绑定随机回环端口，模型流量只发往本机模拟上游。

## 独立复现旧实现失败

在工程根目录导出修复前业务代码，再复制当前的测试注册表和新增测试；只在新临时目录内构建，不改当前工作区或真实数据：

```powershell
$relayRepro = Join-Path $env:TEMP ('household-relay-rate-limit-repro-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $relayRepro | Out-Null
git archive 57a68fd8d257c193fd8e0cb26e07c6de31da4944 --format=zip -o (Join-Path $relayRepro 'baseline.zip')
Expand-Archive -LiteralPath (Join-Path $relayRepro 'baseline.zip') -DestinationPath $relayRepro
Copy-Item -LiteralPath 'HouseholdRelay.Tests/Program.cs','HouseholdRelay.Tests/WebRateLimitTests.cs' -Destination (Join-Path $relayRepro 'HouseholdRelay.Tests')
dotnet run --project (Join-Path $relayRepro 'HouseholdRelay.Tests') -c Release -- 'web rate limit'
```

预期退出码 1：四项限制绕过用例失败，一项第五次合法尝试正例通过。当前实现用相同过滤词应为 5/5 通过。原始本机修复前后输出保留在上表列出的日志中。

## 边界

限速仍为单进程固定窗口，重启服务会重置窗口；本次不增加持久化锁定或跨实例共享计数。管理入口仍仅回环；上游网页配置、可信公网接入和个人用量属于后续工作。验证未使用真实上游凭据，未部署或推送。
