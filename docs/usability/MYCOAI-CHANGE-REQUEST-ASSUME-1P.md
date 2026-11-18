# 修改请求：启动经中转的 Claude Code 时自动带上「按官方直连发请求」开关

| 项 | 内容 |
|---|---|
| 提出方 | HouseholdRelay（家用中转） |
| 交付对象 | MyCoAI 研发团队 |
| 日期 | 2026-11-15 |
| 优先级 | 中（不改软件也能用，但要家人每台手工配，易漏） |
| 关联 | HouseholdRelay 订单 133；家里电脑的手工配置法见同目录 `ASSUME-FIRST-PARTY-SWITCH.md` |

---

## 零、一页速览

**要做的事**：MyCoAI 启动 Claude Code 子进程时，如果这个子进程是经 HouseholdRelay 中转连官方账户（环境里 `ANTHROPIC_BASE_URL` 指向中转），就在子进程环境里加一个变量：

```
_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1
```

只加这一项，其余不动。

**为什么**：不加这个开关，Claude Code 连自定义地址时会少发一批只给官方直连的字段（见第三节）。这些字段只能由 Claude Code 自己生成、中转补不了。加上开关，Claude Code 就把中转当官方地址、自己把这些字段补齐，中转转发后官方看到的请求就和原生直连一致。

**为什么由 MyCoAI 做**：现在靠家人在每台电脑手工设环境变量，容易漏、换电脑要重来。由启动 Claude Code 的 MyCoAI 统一带上，才是长久之计。

**风险**：极低。实测开关不新增任何网络连接、不改变对话/工具/流式行为（见第四节）。它是 Claude Code 内部开关，升级后若失效，只是退回不开的样子，请求照常，不会报错。

---

## 一、背景

HouseholdRelay 把家里几台电脑的 Claude Code 请求，统一经一台中转电脑发往同一个官方账户。目标：让官方看到的请求，和那台中转电脑上原生直连发出的请求尽量一致——这样多台电脑在官方那边表现为同一台设备、同一个账户。

中转负责把「所有电脑都该一样」的部分（设备编号、账户编号、稳定身份头）换成中转电脑的真实值。但另一类字段随会话、提示、每次请求变化，还要和请求正文互相对得上，中转无法代为生成——这正是这个开关要解决的。

## 二、要 MyCoAI 做的改动

启动 Claude Code 子进程、组装环境变量时，按第五节的判定逻辑，在满足条件时追加一个环境变量：

- 变量名：`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`
- 值：`1`

建议做成「连接配置 / 上游配置」上的一个开关项，对 HouseholdRelay 这类中转默认打开，用户可见可改。

## 三、不加开关会缺什么（技术依据）

Claude Code 连自定义地址（即中转）时，默认少发这些只给官方直连的内容：

- **anthropic-beta 少 4 项**：`cache-diagnosis`、`prompt-caching-scope`、`thinking-binding-controls`、`tool-provenance`
- **正文顶层少 `diagnostics` 字段**
- **少 5 个请求头**：`x-claude-code-request-class`、`x-claude-1p-request-class`、`x-claude-code-prompt-id`、`x-claude-code-sandy-plover`、`x-client-request-id`
- **system 计费头少扩展段**：`cch`、`cc_prompt_id`、`cc_turn_origin`、`cc_prompt_index`、`cc_turn_index`

这些值都和本次会话、提示、请求内容绑定，必须由 Claude Code 自己当场生成。开关打开后，Claude Code 判定「当前地址算官方」，就会把以上全部补齐。

**二进制依据**（Claude Code 2.1.323，`bin/claude.exe`，SHA256 `5064E6E0572DCA86257363CA04D48653EEF20EF27F1BD04B504ED7199ED3FD13`）：地址是否算官方由内部函数 `Ri()`（偏移 `0x1232a73d`）判定，它先看本开关（`u2()` 读 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`），再看主机是不是 `api.anthropic.com`。

## 四、实测结论（回环实验，无运维风险）

在中转电脑上做隔离回环实验：Claude Code 专用副本、只拦它的非回环出站、假令牌、本机应答从不转发。对比「不开开关」「开开关」两组，各跑一问、接着问、调用工具、流式中断四种场景：

1. **不新增连接**：两组每次运行都同样被拦下 2 次连向 `api.anthropic.com` 的非必要流量，开关带来的新增连接为 **0**。
2. **路由不变**：经中转的请求只有 `HEAD /api/hello` 和 `POST /v1/messages`，无任何新路由。
3. **行为一致**：对话、工具调用、流式中断，两组表现完全相同。
4. **settings.json 同样生效**：写进 Claude Code 的 `settings.json` 的 `env` 段也能打开，与环境变量等效。

## 五、判定逻辑（建议实现）

```
组装 Claude Code 子进程环境时：

  base_url = 子进程环境里的 ANTHROPIC_BASE_URL

  # 1) 用户已显式设过就不动（包括设成 0 或空）
  if 环境/配置里用户已显式设置 _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL:
      保持用户的值，结束

  # 2) 只对「经中转连官方账户」打开
  if base_url 指向 HouseholdRelay 中转:        # 即该上游是官方账户类中转
      设 _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL = 1

  # 3) 其余情况不加
  #   - 直连官方（无 ANTHROPIC_BASE_URL，或就是 https://api.anthropic.com）：加了也无害，但无必要，不加
  #   - 连第三方 API 中转：不加（开关会让客户端多发上述字段，第三方可能不认而拒绝）
```

**只加这一项**：不要顺带加 `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`（会改变客户端流量）或 `CLAUDE_CODE_GATEWAY_HINT_HEADERS`（在本开关下多余）。

## 六、升级 Claude Code 后的复核

这是 Claude Code 的内部开关，名字以下划线开头、官方文档未公开。MyCoAI 每次升级 Claude Code 后，建议在新二进制里确认变量名仍存在（搜字面量 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`）：

- 仍存在：照常。
- 已改名/移除：在诊断信息里提示「开关已失效」即可，**不要报错、不要阻止启动**——失效只是退回不开开关的样子，请求照常能发。

## 七、验收标准

1. 用 MyCoAI 启动一个经 HouseholdRelay 中转的会话，子进程环境里能读到 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1`（如让 Claude Code 执行 `echo $env:_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`，或用 Process Explorer 看子进程环境）。
2. 直连官方的会话、连第三方地址的会话里**没有**这一项；用户自己设过的值**未被改写**。
3. 升级 Claude Code 后复核逻辑按第六节工作。
4. （中转侧，HouseholdRelay 自行核对，MyCoAI 不用管）真实线路对照时，经中转的请求带上了 `x-client-request-id`、`diagnostics` 及第三节其余字段。

## 附录：相关文件

- 家里每台电脑的三种手工配置法（环境变量 / 用户变量 / settings.json）：`docs/usability/ASSUME-FIRST-PARTY-SWITCH.md`
- 开关对线上字段影响的完整实测报告：HouseholdRelay 仓库 `artifacts/order-133/stage2/assume1p-report.md`
