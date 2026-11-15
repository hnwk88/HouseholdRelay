# 修改请求：MyCoAI 启动经中转的 Claude Code 时自动带上「按官方直连发请求」开关

提出方：HouseholdRelay（家用中转）。日期：2026-11-15。
对应：HouseholdRelay 订单 133 第二段；家里电脑的手工配置法见同目录 `ASSUME-FIRST-PARTY-SWITCH.md`。

## 一、要什么

MyCoAI 启动 Claude Code 子进程、并且这个子进程经中转连官方账户时（环境里设了 `ANTHROPIC_BASE_URL`，指向 HouseholdRelay 中转），在子进程环境里加一项：

```
_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1
```

只加这一项，其余环境照旧。

## 二、为什么

HouseholdRelay 把家里几台电脑的 Claude Code 请求，统一经一台中转电脑发往同一个官方账户，目标是让官方看到的请求和那台电脑上原生直连一模一样。

Claude Code 连自定义地址时，会少发一批只给官方直连的值：

- anthropic-beta 里少 4 项：cache-diagnosis、prompt-caching-scope、thinking-binding-controls、tool-provenance；
- 正文顶层少 `diagnostics`；
- 少 5 个请求头：x-claude-code-request-class、x-claude-1p-request-class、x-claude-code-prompt-id、x-claude-code-sandy-plover、x-client-request-id；
- system 里的计费头少扩展段（cch、cc_prompt_id、cc_turn_origin、cc_prompt_index、cc_turn_index）。

这些值都随会话、提示、请求变化，还要和正文互相对得上，中转没法替客户端补，只能由 Claude Code 自己生成。这个开关让 Claude Code 把中转当成官方地址，自己当场生成全部这些值。中转只负责把真正统一的部分（设备编号、账户编号、稳定身份头）换成中转那台电脑的真实值。

现在家里靠在终端里手动设环境变量，容易漏、换电脑要重来。由启动 Claude Code 的 MyCoAI 统一带上，才是长久之计。

## 三、已有的实测依据

Claude Code 2.1.323（SHA256 5064E6E0572DCA86257363CA04D48653EEF20EF27F1BD04B504ED7199ED3FD13），在中转电脑上做回环实验：专用副本、只拦它的非回环出站、假令牌，本机应答从不转发。对比「不开」「开」两组，各跑一问、接着问、调工具、流式中断：

1. 不多出任何连接：两组每次运行都同样被拦下 2 次连向 api.anthropic.com 的非必要流量，开关多出 0。
2. 经中转的路由只有 `HEAD /api/hello` 和 `POST /v1/messages`。
3. 对话、工具、流式中断两组表现一样。
4. 开关写进 `settings.json` 的 `env` 也同样生效。

二进制依据：地址是否算官方由 Ri() 判定（偏移 0x1232a73d），它先看这个开关（u2()），再看主机是不是 api.anthropic.com。

## 四、具体要求

1. **什么时候加**：子进程的 `ANTHROPIC_BASE_URL` 指向 HouseholdRelay 中转时加。MyCoAI 如果有「连接配置」或「上游配置」的概念，建议做成该配置上的一个勾选项，HouseholdRelay 类配置默认勾上。
2. **什么时候不加**：
   - 直连官方（没设 `ANTHROPIC_BASE_URL`，或它就是 `https://api.anthropic.com`）：不必加，加了也无影响；
   - 连第三方 API 中转：默认不加。开关会让客户端多发上面那些字段，第三方可能不认。
3. **不覆盖用户设置**：用户或系统环境里已经显式设了这个变量（包括设成 `0` 或空），按用户的来，不改写。
4. **只加这一项**：不要顺带加 `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`、`CLAUDE_CODE_GATEWAY_HINT_HEADERS` 等。前者会改变客户端流量，后者在这个开关下是多余的。
5. **可以显示**：它不是秘密，可以在诊断信息里显示「已开启」。
6. **升级 Claude Code 后要复核**：这是 Claude Code 内部开关（名字以下划线开头，官方文档没写）。MyCoAI 每次升级 Claude Code 后，请在二进制里确认这个变量名还在（搜字面量 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`）。不在了就提示「开关已失效」，不要报错、不要阻止启动：失效只是退回不开时的样子，请求照常。

## 五、怎么验收

1. 用 MyCoAI 启动一个经 HouseholdRelay 中转的 Claude Code 会话，在子进程环境里能看到 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1`（例如用 Process Explorer 看环境，或让 Claude Code 执行 `echo $env:_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`）。
2. 直连官方的会话、连第三方地址的会话里没有这一项，用户自己设过的值没被改。
3. 中转一侧：HouseholdRelay 在真实线路对照时，能看到经中转的请求带上了 x-client-request-id 和 diagnostics。这一条由 HouseholdRelay 工程师核对，MyCoAI 不用管。
