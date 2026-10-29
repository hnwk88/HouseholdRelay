# 60 号单验收记录

2026-10-28，总监独立验收。对象为提交 `57a68fd8d257c193fd8e0cb26e07c6de31da4944`，订单“易用性 1/4：SQLite、网页首次设置与个人令牌”。已读取完整任务书、交付结果和 UX-001、UX-002、UX-003 台账，并从“待验收”进入“验收中”后开展检查。

**结论：不通过，退回待整改。** 首次设置和管理登录的限速可被有效路由别名绕过，未满足本单必须实施第 2 项。后续 61—63 号单继续待派单。

阻断项位于 `HouseholdRelay/ManagementWeb.cs:66`：限流选择器只识别字面路径 `/Login` 和 `/Setup`，其他路径进入无限制分区；Razor Pages 同时接受 `/Login/` 与 `/Setup/`。因此给路径加尾斜杠即可持续执行相同处理器，不消耗原限流预算。现有 `HouseholdRelay.Tests/WebSecurityTests.cs:68` 起的用例只尝试标准登录路径，未覆盖这一差异。

在本轮重新发布的程序、全新临时目录和有效 CSRF 会话中，实际 HTTP 结果如下；全部发生在同一限流窗口内：

| 请求 | 实际结果 |
| --- | --- |
| 错误引导码 POST `/Setup` 六次 | 200、200、200、200、200、429 |
| 随后错误引导码 POST `/Setup/` 八次 | 全部 200，继续执行首次设置处理器 |
| 随后正确引导码 POST `/Setup/` | 302，成功创建管理员 |
| 错误密码 POST `/Login` 六次 | 200、200、200、200、200、429 |
| 随后错误密码 POST `/Login/` 八次 | 全部 200，继续验证密码 |
| 随后正确密码 POST `/Login/` | 302，随后访问受保护 `/Users` 返回 200 |

该问题使本机可访问管理端口的请求绕过口令尝试次数限制。当前入口仍受回环、Host、Origin、CSRF 和凭据验证约束；本轮没有发现或宣称无需密码即可登录。

工程师整改要求：

1. 以实际匹配的页面端点或统一的路由身份选择限流策略，让同一页面的全部有效别名共享每分钟 5 次的预算；也可明确拒绝非规范路径，确保请求不会执行表单操作。覆盖登录与首次设置两个入口。
2. 添加实际 HTTP 回归用例：标准路径耗尽预算后，尾斜杠、大小写等框架可接受形式不能继续尝试；交替路径也不能取得额外预算；确认拒绝时没有新增管理员、会话或其他副作用。仅检查第六次标准路径请求不够。
3. 重新跑 Release 构建、完整测试和浏览器旅程；提供修复前用例失败、修复后通过的完整输出及新提交标识，再交回待验收。

其他独立检查已完成：

| 检查 | 本轮结果 | 完整本机证据 |
| --- | --- | --- |
| Release 构建 | 成功，0 警告、0 错误 | `artifacts/acceptance-order-60/build.log` |
| 原十项与新增七项测试 | 17/17 通过；模型流量只到本机模拟上游 | `artifacts/acceptance-order-60/tests.log` |
| 重新发布 | 成功 | `artifacts/acceptance-order-60/publish.log` |
| Microsoft Edge 156.0.4314.50 实际浏览器旅程 | 首次设置、登录、甲乙创建、停用/轮换、网页与独立 CLI 并发、设置保存、重启及退出均通过 | `artifacts/acceptance-order-60/browser.log`、`browser-steps.md` |
| 页面目视检查 | 六张脱敏截图已检查，含首次设置、概览、一次性令牌、使用者、设置和 390px 窄屏；未见水平溢出 | 同目录 `01-first-setup.png` 至 `06-mobile-after-restart.png` |
| 受控迁移反例 | 仅在临时源码副本省略 `LegacyImport.Run` 最后一处 `transaction.Commit()`；构建仍成功，两项迁移测试按预期失败，退出码 1 | `mutation-build.log`、`mutation-tests.log` |
| 额外安全探针 | 复现上述两个入口的限速绕过，退出码 1 表示成功复现缺陷 | `security-probe.cjs`、`security-probe.log` |
| L1 门禁 | `passed` / `ALL_PASSED`，0 错误、0 提醒；按增量台账执行 | `artifacts/gate-reports/beb539e7fadf4fa389aeb7d0e3bef110.json` |

复现正常构建、测试和浏览器检查（PowerShell，工程根目录）：

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/acceptance-order-60/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/browser-journey.cjs artifacts/acceptance-order-60/publish/HouseholdRelay.dll artifacts/acceptance-order-60
node artifacts/acceptance-order-60/security-probe.cjs artifacts/acceptance-order-60/publish/HouseholdRelay.dll
```

Playwright 使用已有临时工具目录内的 1.56.1；缺少时按交付验证文档安装。安全探针是本次缺陷的复现程序，记录的是当前错误行为，修复验收须使用上述要求的新回归断言。

迁移反例从当前提交用 `git archive` 导出独立副本，仅改上述一处提交语句，再构建并执行 `dotnet run --project <副本>/HouseholdRelay.Tests -c Release --no-build -- migration`。完整失败分别落在旧令牌迁移断言和“回滚后恰好导入一次”断言。主工作区业务代码和测试代码均未修改。

本轮验证使用独立临时数据，未部署或推送。网页上游直接配置、可信公网接入和个人用量属于后续订单，本单没有据此宣称整体需求完成。已通过的现有用例与门禁不能覆盖本次独立复现的限速缺陷，整改后须重新验收。
