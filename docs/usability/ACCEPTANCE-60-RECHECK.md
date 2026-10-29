# 60 号单整改复验记录

2026-10-28，总监独立复验。验收对象为 `e8d3878ad53ce6be6857f24ff8d23bc97a6c0d51`，包含基础实现 `57a68fd` 和本次管理表单限速修复。已读取订单完整任务书、首次验收、整改进度、交付结果与 UX-001、UX-002、UX-003 全文，确认订单为待验收后进入验收中。

**结论：通过。** [首次验收](ACCEPTANCE-60.md)发现的 `/Login/`、`/Setup/` 限速绕过已修复，新增回归能识别原缺陷，正常管理流程和原有能力回归通过。60 号单可进入已验收；后续按既定顺序仅派出 61 号单，62、63 继续待派单。

业务差异限定在 `ManagementWeb` 的限流分区选择：`UseRouting` 之后读取已匹配页面的 `PageActionDescriptor.ViewEnginePath`。同一页面的标准路径、尾斜杠、大小写及查询参数形式使用同一个预算，登录与首次设置各保留每分钟 5 次 POST 的限制。未复制一套 URL 路由规则。

本轮实际 HTTP 复验结果：

| 场景 | 实测结果 |
| --- | --- |
| 标准路径累计五次错误凭据后，提交正确凭据 | 标准及全部被测别名均为 429，`Retry-After: 60` |
| 标准路径、尾斜杠、大小写、查询参数交替累计五次 | 全部被测别名共用预算，随后均为 429 |
| 换用另一 Cookie 会话 | 无新增尝试预算，无管理 Cookie 签发，受保护页面仍要求登录 |
| 首次设置超限 | 管理员 0、会话 0、引导凭据 1；正确引导码未被消耗 |
| 管理登录超限 | 管理员 1、会话 0；没有创建管理会话 |
| 第五次提交正确凭据 | 首次设置和登录均成功；两页面预算独立，GET 表单仍可访问 |

没有只依赖工程师的成功总结。已读取其完整修复前后输出，独立重新构建和执行全部 22 项测试，并在隔离副本恢复旧的 `ManagementWeb.cs`。该副本保留本次新测试：构建仍为 0 警告、0 错误，五项限速用例中四项按预期失败、一项正例通过，退出码 1。失败直接显示超限后旧逻辑仍创建管理员或会话，证明新用例覆盖了原故障。主工作区业务代码与测试代码未改动。

本轮独立证据位于 `artifacts/usability-foundation/director-recheck-60/`：

| 检查 | 本轮结果 | 证据文件 |
| --- | --- | --- |
| Release 构建 | 成功，0 警告、0 错误 | `build.log` |
| 完整回归 | 22/22 通过，含原 17 项和新增 5 项 | `tests.log` |
| 发布 | 成功，浏览器测试使用本轮重新发布的程序 | `publish.log`、`publish/` |
| 真实 Edge 浏览器流程 | 首次设置、登录、创建甲乙、停用和轮换、网页与独立 CLI 并发修改、保存配置、重启恢复、退出均通过 | `browser.log`、`browser-steps.md` |
| 视觉核对 | 已目视检查本轮首次设置、保存设置和 390px 窄屏截图；窄屏无水平溢出 | `01-first-setup.png`、`05-settings.png`、`06-mobile-after-restart.png` |
| 恢复旧限速逻辑的受控反例 | 构建成功，四项限制用例失败、一项正例通过，符合预期 | `mutation-build.log`、`mutation-tests.log`、`mutation-source/` |
| L1 门禁 | `notChecked` / `LEDGER_ALL_REUSED`：无需检查，复用台账，0 新诊断；未声称本轮重新执行全部规则 | `gate-l1.json` |

L1 原始运行记录：`artifacts/gate-reports/53ed45962268416aa2c8bc0c922a45d0.json`。工程师提交时的 L2 通过回执另见 `artifacts/usability-foundation/rate-limit-rework/commit.log`。

复跑本轮正常验证（PowerShell，工程根目录）：

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/usability-foundation/director-recheck-60/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/browser-journey.cjs artifacts/usability-foundation/director-recheck-60/publish/HouseholdRelay.dll artifacts/usability-foundation/director-recheck-60
```

反例副本从当前提交通过 `git archive` 导出，仅将其中 `HouseholdRelay/ManagementWeb.cs` 换成 `57a68fd` 的原文件。构建副本后运行 `dotnet run --project <副本>/HouseholdRelay.Tests -c Release --no-build -- 'web rate limit'`，预期为 1/5 通过、退出码 1；该输出属于故意恢复缺陷的检验，不代表交付代码失败。副本作为本机复验证据保留，不用于部署。

本次管理与持久化基础验收完成。限速仍为单进程固定窗口，重启会重置窗口；管理仅支持本机回环。浏览器为 Edge 156.0.4314.50，Playwright 为 1.56.1；所有业务测试使用独立临时数据，模型流量仅发往本机模拟上游。账户/API 网页直接配置、可信公网接入和个人用量仍属后续订单，整体需求尚未完成。本轮未部署或推送。
