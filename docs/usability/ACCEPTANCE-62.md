# 订单 62 验收记录

验收日期：2026-10-28 至 2026-10-29（Asia/Shanghai）。验收角色：总监。验收提交：`e3926b8bb42321134a78f39ac02d8d17e2369467`。已读取订单 62 全文、UX-001/002/003 全文及最新私人使用批示，先将订单从“待验收”转为“验收中”，再执行以下独立复核。

## 结论

**通过订单 62：个人用量、金额与时间统计、持久化查询及滚动 40 天清理。** 独立 Release 构建成功，0 警告、0 错误；完整回归 54/54；发布程序的网页流程通过；两家真实 API 的统计比对及客户端流程复核通过。受控变异能够使关键用例失败，恢复后通过。验收未修改工作区的产品代码或测试代码。

金额沿用用户在订单中的补充决定：Token 以上游返回为准；明确带币种的上游费用优先，未返回费用时允许管理员配置费率快照估算并标明来源，没有依据则显示未知。真实 API 本轮均未返回明确费用，页面保持未知；上游报告金额、估算金额、币种隔离及历史快照使用可控上游验证。

本结论按最新私人使用路线作阶段验收。私有 CA HTTPS、目标设备一次性信任引导和完整交付旅程归订单 63。真实官方账户和公网部署按用户决定后置。

## 独立验证及结果

| 检查 | 本轮实际结果 | 原始证据 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | `artifacts/acceptance-62/build.log` |
| 完整回归 | 54/54，包含 39 项前置回归和 15 项用量验证；已阅读完整输出 | `artifacts/acceptance-62/tests.log` |
| 发布 | 当前提交的 Release 发布成功 | `artifacts/acceptance-62/publish.log`、`publish/` |
| 网页流程 | Edge 156.0.4314.50；全新设置、两人归属、已知/未知、金额来源、小时及区间筛选、改价、轮换/改名/停用、越权拒绝、杀进程恢复均通过 | `artifacts/acceptance-62/browser.log`、`browser/browser-results.json` |
| 页面目视检查 | 六张模拟截图及两张真实用量截图已逐张查看；390px 表单可用，宽表在独立区域滚动，页面无水平溢出 | `browser/01-pricing.png` 至 `06-mobile-personal.png`；`real-recheck/*-usage.png` |
| SQLite 夹具独立查询 | 完整性检查为 ok；甲 5 条、乙 2 条；一条中断记录保留 NULL；历史估算仍为 0.00011 USD；查询使用 usage_user_time 覆盖索引 | `artifacts/acceptance-62/database-check.json`、`browser/usage-fixture.db` |
| 并发与清理 | 48 个并发请求加 10 次管理写入/查询，实测 2708 ms；持久化失败 0；10000 条过期数据按 500 条批次清理，分页 25/23 条无重复 | `tests.log` 中 USAGE LOAD 及相关 PASS |
| 受控反例 | 隔离副本中把 SSE output_tokens 更新改成相加，累计用例因 complete cumulative tokens 失败，退出码 1；恢复原文件后 1/1，退出码 0 | `artifacts/acceptance-62/mutation-sse.log`、`mutation-restored.log` |
| L1 门禁复查 | `notChecked / LEDGER_ALL_REUSED`（无需检查）；沿用已有结论，0 错误、0 提醒、0 欠账。本轮未重新执行判据，源码编码另有 5 个存量文件留给全量级别 | `artifacts/gate-reports/423d71a046dc4f678041ed544e06a819.json` |

回归具体覆盖 JSON 完整/部分/缺失、SSE 累计 1/4/9 及重复事件、逐字节 UTF-8 分片、未知事件、错误/缺失终止、取消/超时、有界事件解析、幂等结束保存、双模式稳定身份、停用与越权、锁库和不可写、起始及发送标记失败不发上游、结束失败可见、schema 回滚、正常停服与异常恢复。固定时钟覆盖 40 天前一 tick、恰好边界、边界后一 tick、每小时清理及长停机启动；时区分组覆盖夏令时回拨，UTC 保留边界不变。

源码核对了 RelayServer 的记录生命周期、UsageStore 的短事务及独立结束保存、UsageAccumulator 的累计语义、UsageQuery 的共同窗口/只读快照/参数化筛选、PersonalUsage 的令牌身份边界、UsagePricing 的金额来源及价格快照，以及管理和个人页面。未发现阻止本单通过的实现问题。

## 两家真实 API 复核

使用本项目既有预检 DPAPI 材料，通过匿名管道交给隔离浏览器流程；未读取官方账户或其他应用账户。客户端 Claude Code 2.1.311，模型 claude-opus-5-5。下游是本机 HTTP，上游 HTTPS 保留正常 TLS 校验。

| 上游 | 场景 | 输入 / 输出 | 缓存读 / 写 | 持久化结果 |
| --- | --- | --- | --- | --- |
| aitoll.net/v1/cc，API/Bearer | JSON | 2 / 4 | 1595 / 0 | 四列相等，完整 |
| 同上 | SSE | 2 / 4 | 1595 / 0 | 四列相等，完整 |
| 同上 | 重启后 JSON | 2 / 4 | 1595 / 0 | 四列相等，旧记录保留 |
| api.sftoken.top，API/x-api-key | JSON | 4 / 5 | 1644 / 0 | 四列相等，完整 |
| 同上 | SSE | 4 / 5 | 1644 / 0 | 四列相等，完整 |
| 同上 | 重启后 JSON | 4 / 5 | 1644 / 0 | 四列相等，旧记录保留 |

两家均经网页配置、测试、激活；管理员检测未增加个人请求数。两家均完成真实 Read/tool_result 两轮会话，最终返回专用标记；切回第一家后再次调用成功。第二家重启后的汇总保留 12 次成功推理，全部金额未知。临时服务已停止。完整结果：`artifacts/acceptance-62/real-recheck/real-upstream-results.json`，控制台：`real-recheck.log`。

### 首轮失败及复核边界

首轮真实流程退出码 1：第一家的网页、普通客户端和 JSON/SSE 用量比对通过，随后工具会话返回成功状态，但最终回复不含测试要求的专用标记。原脚本未保存该次回复，无法据此确定根因。该次失败保留在 `artifacts/acceptance-62/real.log` 和 `real/real-upstream-results.json`，不计作通过。

随后在隔离的验收脚本副本中，仅增加针对专用合成提示的脱敏结果诊断，保留全部原断言，产品二进制不变；再次执行完整流程，两家及切回均成功，退出码 0。诊断在 `real-recheck/native-diagnostics.jsonl`，显示两次工具会话均为 2 轮且返回 HOUSEHOLD_REAL_TOOL_OK。首轮原因仍未确证，不能写成已经修复或已证明属于上游波动。63 顺路完善真实验收脚本的失败诊断并观察复现情况，不自动重试掩盖失败。

## 复现

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/acceptance-62/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/usage-browser.cjs artifacts/acceptance-62/publish/HouseholdRelay.dll artifacts/acceptance-62/browser
```

真实 API 的原始命令为验证文档中的 `--real-upstream-browser`，发布 DLL 和输出目录改为本轮目录。第二轮运行位置为 `mutation-path.txt` 指向的隔离源码副本，该副本只对真实浏览器脚本增加脱敏诊断；源码累计计数的受控变异已恢复。所有测试结果均使用当前提交的产品实现。

后续由总监将本结论写回 62 并关闭，再按顺序派发唯一下一张 63。63 继续按最新任务书完成私人版本交付，不重开真实官方账户或公网部署前置。
