# 私人部署验证记录

验证日期：2026-10-29，Windows x64、.NET SDK 10.0.302、Edge 156.0.4314.50、原生 Claude Code 2.1.311。最终实现提交为 d36f301（承接 e3926b8）。

**最终交付条件已完成（完整模式复核通过）。** 实现、57 项模拟回归、三组网页旅程和两家真实 API 的 HTTPS 验证已经完成。用户在 Windows 安全确认窗口核对并完成了当前用户根信任安装，随后完整模式复核通过；订单可交回待验收。此前两次等待超时仍作为失败记录保留，不能替代本次成功证据。

最终完整模式运行结果保存在 `artifacts/private-deployment/real-system-check/real-upstream-results.json`。本轮 CA SHA-256 为 `B7A3100E0E2D8A0CBE5FE430CE07F716ED5955567D58077E10C2142215004E0C`；系统 TLS 状态为 200，重复安装通过，Edge 信任通过，原生 Claude Code 2.1.311 的 JSON、SSE、Read/tool_result、切换、重启、轮换和停用均通过，收尾已恢复用户信任环境（`trust_restored: true`）。

## 实现范围

- HTTPS 网页创建私有 CA 和匹配地址的服务器证书，沿用配置版本事务、SQLite 和 DPAPI；替换证书须明确确认重新分发信任。
- 私有和迁移证书启动时验证名称、有效期、服务器用途及本实例确切根；失败时保留本机管理入口。
- 管理员连接页下载只含 CA 公共证书、Windows 当前用户安装脚本和说明的信任包。脚本先核验 SHA-256，再安装系统根及 Claude Code 的额外 CA；保留已有 CA，显示系统确认使用的 SHA-1。
- 连接页、概览、控制台和部署说明采用私人路线。表单提供等待提示，复制有状态反馈；正常 TLS 校验始终保留。
- 真实验收脚本只对专用合成请求保存有界、脱敏失败诊断，包括阶段、退出码、超时、工具调用/结果数和最终标记；保留全部断言，不自动重试模型请求。业务日志和用量库不增加正文。

## 验证范围及状态

| 项目 | 证据类型 | 实际结果 |
| --- | --- | --- |
| Release 构建 | 本机实际构建 | 0 警告、0 错误；日志见 evidence/private-build.txt |
| 完整 .NET 回归 | 模拟上游、真实进程/TLS | 57/57，包括全部原有 54 项及 3 项私有证书用例 |
| 诊断受控反例 | 合成客户端子进程 | 3/3，错误标记、缺工具回合、退出失败均保持失败，记录脱敏 |
| 首次设置、身份、CSRF、权限、重启 | 无窗口真实 Edge、本机模拟 | 通过；使用独立数据和客户端目录 |
| API/账户切换、凭据失效恢复、PFX 拒绝、私有 CA 页面 | 无窗口真实 Edge、本机模拟 | 通过；含标签、键盘、等待状态、复制和 390px 布局 |
| 已知/未知/金额、小时与区间、归属、停用、意外退出恢复 | 无窗口真实 Edge、本机模拟 | 通过，含可复查 SQLite 夹具 |
| 未信任根、错误根/名称、过期证书 | 实际 TLS 与启动验证 | 拒绝；保持正常链、名称、时间和用途校验 |
| 两家 API 的网页接入、JSON/SSE、工具、切换、重启 | 真实上游、私有 CA HTTPS、原生客户端 | 通过；最终完整模式使用系统信任，见下表和 `real-system-check/` |
| 两人并发、四类 Token、轮换、停用 | 真实上游 HTTPS | 归属和 Token 与响应一致；两家费用均未知 |
| Windows 系统根安装及安装后 Edge 信任 | 系统操作 | **通过**：系统 TLS 200，Edge 无忽略证书错误访问成功，重复安装和清理通过 |
| 真实官方账户、公网域名/映射、其他操作系统 | 尚未执行 | 按私人使用决定后置，不宣称已验证 |

完整回归包括：账户/API 协议、响应及 SSE/头秘密标记、越权、首次设置抢占、CSRF、限流、取消、错误、累计/部分/缺失用量、两人并发、数据库锁与不可写、新旧数据迁移、正常停服排空、异常退出、UTC 40 天边界、每小时及离线跨期清理、分页和查询索引。负载样本为 48 个并发请求加 10 次管理读写，实测 1316 ms；一万条历史记录按批清理，个人查询命中 usage_user_time。该样本不是长期生产性能保证。

## 两家真实 API 与客户端 HTTPS

这次使用发布程序和独立数据目录，网页完成首次设置、私有证书创建、两人令牌、候选测试和激活。模型为 claude-opus-5-5。下游实际连接为 HTTPS 回环地址，证书来自本次网页生成的私有 CA；最终完整模式先完成 Windows 当前用户系统根信任，再由 Edge、Node 和 Claude Code 通过正常证书链校验。未关闭校验，未使用测试伪造的系统信任。

此前的客户端专用 CA 文件运行仍保留在 `artifacts/private-deployment/real/`，用于记录人工确认前的隔离证据；本次 `real-system-check/` 才是系统信任、Edge 和实际客户端的完整模式证据。

| 提供商与配置 | 请求 | 输入 / 输出 | 缓存读 / 写 | 持久化结果 |
| --- | --- | --- | --- | --- |
| aitoll.net/v1/cc，API/Bearer、原生检测 | 甲 JSON | 2 / 5 | 1237 / 357 | 逐项相等 |
| 同上 | 乙 SSE | 2 / 5 | 1237 / 357 | 逐项相等 |
| 同上 | 重启后 JSON | 2 / 5 | 1594 / 0 | 逐项相等，历史保留 |
| api.sftoken.top，API/x-api-key、标准检测 | 甲 JSON | 4 / 5 | 1251 / 392 | 逐项相等 |
| 同上 | 乙 SSE | 4 / 5 | 1643 / 0 | 逐项相等 |
| 同上 | 重启后 JSON | 4 / 5 | 1643 / 0 | 逐项相等，历史保留 |

两家 Read 会话均有 2 回合、一个实际 Read 和一个 tool_result，最终标记符合要求；切回第一家成功。SSE 分别收到 7 和 8 个数据块，首块耗时 2360/3574 ms，结束耗时 2421/3598 ms；独立受控流式用例另外验证完整事件及时刷新及取消释放。管理检测前后个人记录数不变。

两家均未返回带币种的费用，本次没有填假费率，金额保持未知。实际 request_id、使用者 ID、上游版本及四类 Token 见 evidence/private-real-client-results.json，客户端回合元数据见 evidence/private-client-diagnostics.json。

为了取得受限供应商要求的原生请求结构，脚本先让独立 Claude Code 与本机模拟响应交互，只在内存捕获合成请求形状；这一项明确记为 offline-request-shape。其后的真实原生会话、工具会话、JSON、SSE、个人查询均直接连接产品 HTTPS。管理网页及本次个人网页截图使用本机 HTTP；没有把这些截图写成浏览器 HTTPS 已通过。

兼容范围仅为所测 Windows 原生 Claude Code 2.1.311、上述模型和 Anthropic Messages JSON/SSE/Read 工具行为。管理诊断检测模型目录，客户端显式选择模型；未宣称所有客户端自动发现、任意 API 或任意工具兼容。

## 失败记录和仍未确定的原因

前次独立验收的真实工具会话首轮最终回复标记失败，隔离复核随后通过；首轮未保存必要回复，根因至今未确定。原记录在 artifacts/acceptance-62/real/ 与 real-recheck/。本次增加安全诊断后，两家工具会话首次实际 API 运行均通过；不能据此消除原来的不确定性，试运行继续观察。

本次额外记录以下失败，均保留原输出：

1. real-first/：信任安装子进程 30 秒未退出，尚未调用真实 API。定位为 Windows 原生根证书安全确认。
2. real/：为系统确认保留更长等待，仍未完成；未调用真实 API。临时服务、根信任和用户环境已经清理。
3. 原默认构建目录被正在等待确认的验证父进程锁住，构建复制失败；改用独立 artifacts 输出目录后 0 警告/0 错误。不是忽略编译错误。
4. 两次新增浏览器等待状态测试把导航请求拦住后，又等待 Playwright 导航完成，测试自身死锁超时。改为在真实 submit 事件同步记录状态，再执行正常导航；原产品断言保留，最终浏览器流程通过。

## 发布与复现

自包含 Windows x64 候选发布目录为 artifacts/private-deployment/windows-x64/。它带 .NET 运行时、静态网页和私人部署说明；不是含数据或凭据的实例备份。系统安装实测和完整模式复核已完成，可交回待验收。

```powershell
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
dotnet build HouseholdRelay.sln -c Release --artifacts-path artifacts/private-deployment/regression
dotnet artifacts/private-deployment/regression/bin/HouseholdRelay.Tests/release/HouseholdRelay.Tests.dll
node --test HouseholdRelay.Tests/real-validation-support.test.cjs
dotnet publish HouseholdRelay -c Release -r win-x64 --self-contained true --artifacts-path artifacts/private-deployment/package-build -o artifacts/private-deployment/windows-x64
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/browser-journey.cjs artifacts/private-deployment/windows-x64/HouseholdRelay.dll artifacts/private-deployment/foundation-browser-new
node HouseholdRelay.Tests/upstream-browser.cjs artifacts/private-deployment/windows-x64/HouseholdRelay.dll artifacts/private-deployment/upstream-browser-new
node HouseholdRelay.Tests/usage-browser.cjs artifacts/private-deployment/windows-x64/HouseholdRelay.dll artifacts/private-deployment/usage-browser-new
```

真实 API 命令复用本项目以前获准使用的两家预检 DPAPI 材料，通过匿名管道交给浏览器脚本，不读取官方账户凭据或输出上游秘密。需要材料仍可由当前 Windows 用户解密，且会产生少量真实模型消耗：

```powershell
dotnet artifacts/private-deployment/regression/bin/HouseholdRelay.Tests/release/HouseholdRelay.Tests.dll --real-upstream-browser artifacts/upstream-compatibility-20261028/relay-opus-proxy-preflight.json artifacts/upstream-compatibility-20261028/sftoken-api-key-relay.json artifacts/private-deployment/windows-x64/HouseholdRelay.dll artifacts/private-deployment/real-system-check
```

完整模式命令需要操作者在 Windows 安全窗口核对本次 CA 并确认，然后自动检测系统 TLS、无忽略证书错误的 Edge、实际客户端和真实 API，结束恢复原用户信任与环境。本次完整模式已通过，结果见 `artifacts/private-deployment/real-system-check/`。仅独立检查客户端文件信任时在末尾加 `--client-only`，输出会明确把系统安装和浏览器信任标为未执行，不能用该模式替代完整模式。

窗口边界和数据库故障复现入口见 USAGE-VALIDATION.md。当前浏览器夹具在 artifacts/private-deployment/usage-browser/usage-fixture.db，原始输出和截图在各流程目录。已目视检查私有证书桌面页、390px 连接页与个人页；页面内容无截断，表格在独立区域横向滚动。

L1 强制复查为 `passed / ALL_PASSED`，0 个错误、0 个提醒，运行标识 `b49faaee6ee24817812b4b560cea68ce`；最终完整模式结果另见 `real-system-check/real-upstream-results.json`。工程门禁结论与真实系统信任证据分别记录，不能互相替代。
