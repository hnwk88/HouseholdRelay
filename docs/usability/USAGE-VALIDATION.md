# 个人用量、金额与时间统计验证

日期：2026-10-28。实现基线 a8b65cb。本轮交付逐请求持久化、个人与管理员查询、金额来源、小时/天统计和滚动 40 天清理，交回待验收。最新用户补充“类型 Token、金额”和“按时间统计”已经纳入；金额遵循上游返回优先、估算明确标记、没有依据显示未知的约定。

## 使用入口与统计口径

- 管理员：本机管理导航“用量”，可组合使用者、开始时间（含）、结束时间（不含）、接口、模型精确匹配、结果筛选；选择按天或按小时。页内汇总包括全部筛选结果，明细每页 25 条。
- 个人：中转根地址下 `/my-usage`。令牌仅保留在当前页面内存，放在认证头发送，不写 URL、localStorage 或 Cookie；不能指定他人 ID。清除按钮及离开页面清空令牌和结果。
- 请求按不可变使用者/令牌主键归属，不使用姓名或客户端 metadata 认人。记录开始时间、上游版本、接口、请求模型、结果、发送状态、状态码、耗时及四类 Token；不能解析到模型的拒绝请求允许模型未知。
- 推理、Count Tokens 估算、Models 查询分别统计；管理员标准/原生检测、健康、静态页、HEAD 预热和未鉴权流量不混入个人消耗。已鉴权但未发送的拒绝保留失败记录，Token/金额不适用。
- JSON 读取 usage，SSE 读取 message_start/message_delta，累计计数更新而不求和。完整流需开始、最终 delta 和 message_stop；缺少结束、取消、中断、错误保留已知计数并标记不完整。未知事件不影响正常转发。
- 已知合计与缺失数同时展示，SQL NULL 不转换为零。缓存读/写独立列出，不并入普通输入；估算接口不计入实际推理 Token。金额未知数另列，不同币种和来源分组。

## 金额依据和边界

通过 web-query 抓取并读取以下 HTML 正文，均为 HTTP 200、`text/html; charset=utf-8`，不是只读取搜索摘要；未登录、未使用 Cookie 或管理密钥，也未调用组织账单 API。

| 来源 | 最终页面 | 核对结论 |
| --- | --- | --- |
| Create a Message — Claude API Reference | https://platform.claude.com/docs/en/api/messages/create | Messages 返回 input_tokens、output_tokens 和缓存等 usage 字段，没有标准逐请求金额/币种字段 |
| Usage and Cost API — Claude Platform Docs | https://platform.claude.com/docs/en/manage-claude/usage-cost-api | 原 `/docs/en/api/usage-cost-api` 重定向至此；费用是独立 Admin API，组织级 USD 报表按分计，不等于当前 Messages 自动返回的个人费用 |

原始网页、元数据及解析正文在 `artifacts/usage/web-sources/`。标准 Messages 没返回费用时，默认显示未知，不拿客户端价格估算或组织账单合计冒充这次请求的扣费。

可选兼容上游扩展明确约定为 `usage.cost_usd`（美元主单位），或 `usage.cost = { amount, currency }`（三位币种代码对应的主单位金额）。金额需非负有效十进制数；无币种的裸 cost、余额、报价和其他字段不采集。上述扩展不是 Anthropic 官方标准，只有供应商采用同样字段和单位时才适用；不读取或套用官方按分计的组织报表。SSE 中费用也按累计更新，最后已知金额只保存一次。

未返回明确费用时，可在 `/Pricing` 为当前上游版本和模型配置每百万 Token 的输入、输出、缓存读/写单价。记录保存发送时的费率快照；改价不重算旧记录，切换上游版本不暗中沿用旧价。金额采用 decimal 运算、文本持久化和十进制聚合，保留至 9 位小数；上游报告和估算、不同币种都不合并成一个无单位数字。未知单价留空，明确免费才填 0。缺少计数或单价时保留已知估算部分并标为不完整；全无用量仍为未知，不因缓存单价为零变成零费用。估算仅覆盖所配置四类 Token，不含工具等额外收费，不代表供应商账单。

## 持久化、窗口与失败

schema v3 新增 usage_requests、usage_prices 和时间/使用者/类型/模型/结果索引。迁移在事务中进行；发生冲突时 schema 版本和身份保持原状。原 v1 上游迁移也继续经过原机制。

鉴权后的受支持请求先提交起始记录，再排队/上传；发送前独立保存发送标记、实际上游版本、模型及费率快照。起始或发送标记失败返回 503，不能向上游发送。每次数据库操作为短操作，进程内写入通过有界异步队列串行，不持有贯穿网络流的事务。结束保存独立于客户端取消，最多三次，数据库忙等待与队列等待都有上限；失败记录日志 `usage_storage_failure` 并在页面报告，原始 started 记录保留供启动恢复。重复结束更新不会覆盖第一次已提交的结果。

服务启动先取得本实例文件锁，避免另一进程把活跃请求误恢复；随后将遗留 started 标为 interrupted/partial，再清理过期记录。查询/CLI 只是打开数据库时不做恢复。强制退出会丢失尚在内存中的部分计数，恢复记录以未知表示，不伪造成功或零消耗。正常停服排空请求后，完整记录跨重启保持逐字段相等。

默认保留 40 × 24 小时，按 UTC request_started_at 严格 `< now - 40 days` 每批删除 500 条，等于边界保留；启动及每小时执行。每次查询自行应用窗口，不等维护任务。没有另建会漂移的汇总副本，明细、类别汇总、金额与时间汇总使用同一个 SQLite 只读快照。小时分组区分夏令时回拨中重复的两个小时，天分组按指定时区日界；这些显示分组不改变 UTC 清理边界。配置、用户、令牌和费率不清理。

## 验证结果

Release 构建 **0 警告、0 错误**。完整独立测试工程 **54/54 通过**，其中新增 15 组用量验证；默认测试集仅调用本机模拟上游。构建输出、完整测试输出、发布输出分别在 `artifacts/usage/build.log`、`tests.log`、`publish.log`，归档文本见 evidence/usage-tests.txt。

新增覆盖：账户/API 双模式双人并发、改名/轮换/停用和伪造归属；JSON 完整/部分/缺失；SSE 1/4/9 累计、重复 delta、每字节 UTF-8 分片、未知事件、error、缺失终止和未闭合尾事件；流式取消/超时及释放；超过 5 MB 的多事件流和超过 1 MB 的单事件拒绝；锁库、不可写触发器、发送标记失败、结束重试耗尽及页面告警；schema 回滚；固定时钟的 40 天边界、小时清理和长停机启动清理；金额来源、累计费用、精确小数、币种隔离、费率快照、全未知金额；夏令时重复小时、自选区间；费率页管理员/CSRF/版本冲突；正常停服重启。

完整回归中，**48 个并发请求 + 10 次管理写入/查询耗时 2161 ms**，0 次用量保存失败。另批量插入 10000 条过期记录，验证查询先过滤，再按 500 条一批清理；个人窗口查询计划使用 `usage_user_time` 索引，分页的 25/23 条不重复。时间是这台机器本次样本，不是性能保证。

真实无窗口 Edge **156.0.4314.50** 使用发布产物完成全新网页设置、两人调用、未知展示、费率设置、金额来源、按小时和自选区间、改价历史不变、改名/轮换/停用、拒绝个人越权、进程强制结束及恢复。六张截图逐张目视检查，390px 管理与个人页面无页面水平溢出，宽表在独立区域内横向滚动。浏览器流程退出码 0，临时服务停止；完整步骤及数据库夹具在 `artifacts/usage/browser/`。`usage-fixture.db` 是停服后复制的模拟业务数据库，仅供只读统计复查，不是可直接替换生产实例的备份。

## 两家真实 API 的统计核对

复用此前项目专用预检目录的 DPAPI 材料，只读解密后经匿名管道交给浏览器；没有读取 MyCoAI 工作账户，也没有把真实凭据写进命令行、源码、日志、截图或证据。通过网页测试和激活，使用隔离 Claude Code **2.1.311**，模型均为 `claude-opus-5-5`。管理员检测前后个人请求数完全相同。

| 实际上游 | 场景 | 输入 / 输出 | 缓存读 / 写 | 持久化与响应比对 |
| --- | --- | --- | --- | --- |
| aitoll.net/v1/cc，API/Bearer | JSON | 2 / 5 | 1595 / 0 | 相等，完整 |
| 同上 | SSE | 2 / 4 | 1595 / 0 | 相等，完整 |
| 同上 | 重启后 JSON | 2 / 4 | 1595 / 0 | 相等，旧记录仍在 |
| api.sftoken.top，API/x-api-key | JSON | 4 / 4 | 1643 / 0 | 相等，完整 |
| 同上 | SSE | 4 / 5 | 1643 / 0 | 相等，完整 |
| 同上 | 重启后 JSON | 4 / 5 | 1643 / 0 | 相等，旧记录仍在 |

同一次流程还复跑原生简短会话、Read/tool_result、两家切换和再次切回；两家均成功。本次两家响应都未返回明确费用，未配置估算费率，所以金额为未知，未伪造官方金额。独立 request_id、使用者 ID、上游版本、Token、金额状态和客户端结果归档在 [真实统计证据](evidence/usage-real-results.json)。原始结果及真实页面在 `artifacts/usage/real/`，所有测试服务已停止。

这次真实验证使用本机 HTTP 下游和正常校验 TLS 的真实 HTTPS 上游；它验证用量归属和持久化，不宣称已完成后续私人 CA 全旅程或真实官方账户试运行。

## 独立复现

```powershell
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- usage
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/usage/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
node HouseholdRelay.Tests/usage-browser.cjs artifacts/usage/publish/HouseholdRelay.dll artifacts/usage/browser
```

Playwright 依赖只用于测试，沿用本机已有独立工具目录；产品没有 Node 运行或构建依赖。`UsageStorageTests` 可独立构造边界、锁库、结束写失败和恢复反例，`UsageMoneyTests` 构造费率快照和时区反例，`UsageStreamingTests` 构造分片及错误事件，均不需要真实 API。

模拟数据库只读核对可用 SQLite 工具查询：

```sql
SELECT kind,outcome,completeness,COUNT(*),SUM(input_tokens),SUM(output_tokens)
FROM usage_requests GROUP BY kind,outcome,completeness;
SELECT request_id,user_id,model,amount,currency,money_source,money_completeness
FROM usage_requests ORDER BY request_started_at;
EXPLAIN QUERY PLAN SELECT request_id FROM usage_requests
WHERE user_id=? AND request_started_at>=? ORDER BY request_started_at DESC,request_id DESC LIMIT 25;
```

时间列单位是 .NET UTC ticks（100 ns），不是 Unix 秒。SQL SUM 为 NULL 时仍须保留未知语义。浏览器夹具含 7 条记录：甲 5 条（含强制退出的 1 条），乙 2 条；无提示词、回答和明文个人令牌。

真实 API 复现会消耗少量额度，只有显式运行下列命令才进行；预检材料须仍属于当前 Windows 身份可解密的本项目目录：

```powershell
dotnet HouseholdRelay.Tests/bin/Release/net10.0-windows/HouseholdRelay.Tests.dll --real-upstream-browser artifacts/upstream-compatibility-20261028/relay-opus-proxy-preflight.json artifacts/upstream-compatibility-20261028/sftoken-api-key-relay.json artifacts/usage/publish/HouseholdRelay.dll artifacts/usage/real
```

L1 手动复查结果为 `passed / ALL_PASSED`，0 错误、0 提醒；运行标识 6307cfd8020c488790cf50db8506aa81。提交时由现有 L2 挂点检查暂存内容，未跳过挂点或变更门禁配置。
