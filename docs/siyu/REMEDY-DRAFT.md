# 「打回」协商草案（已并入协议文本 2.0，只留作记录）

> **已并入**：2026-11-24 两边总监对齐后，本草案由订单 180 并入 [PROTOCOL.md](PROTOCOL.md) 文本版本 2.0（第 9、10 节）并实现。以协议文本为准，本文件不再修改。与草案不同的几处：版本 2 不许带 `officialAccountId`；`identity-version-stale` 的代次只放在打回里；打回表多了 `session-not-found`；后端切到标准上游另有标准模式（协议文本第 4.3、9 节）。

草案版本 0.1，2026-11-24，HouseholdRelay 工程师起草（订单 174）。两边总监对齐后才写进 [PROTOCOL.md](PROTOCOL.md)、才实现；MyCoAI 一侧放在它的第 5 批切换件里接。对齐之前，线上协议照 PROTOCOL.md 版本 1 不变。

## 1. 要解决什么

用户的原则（2026-11-24）：中转是后端，核对客户端发来的对不对；不对就告诉客户端什么是对的，客户端照做、重发，中转核对通过就放行，这样就无人值守。后端换了账户，客户端自动接着走。只在两方协议范围内。

## 2. 今天已有的雏形

- 建会话带了过时的身份代次：409 `identity-version-stale`，错误体带 `currentIdentityVersion`（`src/Application/Services/Siyu/SiyuSessionService.cs:99-100`、`src/HouseholdRelay/Siyu/SiyuControl.cs:150`）。MyCoAI 用这个代次重建会话即可。
- 「对的值」今天只有一个来源：建会话回应里的 `launchIdentity`（设备编号、官方账户资料、凭据资料）与 `proxy`（代理地址、票据、代理 CA）。
- 会话结束时，查询与续租回 409，带结束原因码与读句；MyCoAI 每 40 秒续租一次，这是它发现会话结束的固定时机。

## 3. 做法：打回只说「哪一类不对、下一步做什么」，对的值仍只从建会话回应一处给

拒绝体在今天的形状上加一个 `remedy`：

```text
{ "type": "error", "error": { "type": "siyu_refused", "code": "<原因码>", "message": "<读句>",
  "remedy": { "action": "recreate" | "wait", "expectedIdentityVersion": <整数，可无>, "retryAfterSeconds": <整数，只有 wait 才有> } } }
```

- `recreate`：MyCoAI 结束本地会话，用新的 `requestId` 重新建会话（带上 `expectedIdentityVersion`，有就用它），把 201 回应里的 `launchIdentity` 写进 PROTOCOL.md 第 4.1 节的三样，再起进程。
- `wait`：过 `retryAfterSeconds` 秒再建会话，用于维护中、名单暂时读不到这类会自己好的情形。
- 没有 `remedy` 的拒绝要人来处理，MyCoAI 停下并读出读句。

为什么不把整份身份直接放进拒绝体：那样「对的值」就有两个来源（建会话回应和拒绝体），两边各自解析、各自写文件，迟早不一致。现在的做法里拒绝体只带「下一次建会话要用的那一个数」，身份照旧只从建会话回应来，仍是一次往返、无人值守。

## 4. 写死的四条

1. **打回能改的东西只限白名单**。自动处理只会改这些：`.claude.json` 的 `userID` 与 `oauthAccount`、`.credentials.json` 的 `claudeAiOauth` 三项资料、下一次建会话的 `expectedIdentityVersion`、进程环境里的代理地址、票据与代理 CA（PROTOCOL.md 第 4.2 节那张表）。**不许**改、也不许下发：个人令牌（它是这条通道的认证本身，失效了要人来换）、任何官方令牌、用户本地的权限、技能、挂点、设置、插件、MCP 配置、别的环境变量、任何文件路径或命令。回应里出现白名单以外的键，MyCoAI 当协议错误处理：停下、读出读句，不照做。
2. **重试有上限**。同一个启动方案在一次运行里，同一个原因码连续自动处理最多 2 次，所有原因码合计 10 分钟内最多 3 次；到上限就停下，读出最后一次的读句，并说「已经自动重试了几次」。身份不符一类（第 6 节）只自动处理 1 次：文件已经按建会话回应写过还不符，说明是别的原因，要人看。
3. **每次打回与改了什么都留痕**。中转在自己的日志里记：时刻、使用者主键、令牌主键、会话编号、原因码、`remedy.action`、给出的身份代次；不记令牌、票据、邮箱或资料原文。MyCoAI 在活动记录里记：时刻、原因码、做了什么、改了哪几个键、改前改后的身份代次与资料代次。
4. **只认验证过的控制通道**。只有控制面的回应才可能带 `remedy`：经站点可信 HTTPS（证书链到 D-002 的站点私有 CA，主机是配置里的站点），并且这次请求带了个人令牌。代理通道上的拒绝（claude.exe 收到的那些）不带 `remedy`，MyCoAI 也不去解析它们当指令；官方的回应更不算。

## 5. 后端选账户（协议版本 2 的候选，对应 2026-11-24 用户批示）

用户定了「后端接的是哪一个、换成了哪一个，只有后端知道、后端决定，客户端不判断」。今天的版本 1 要客户端在建会话时带 `officialAccountId`，与这条不一致。草案：

- 建会话的 `officialAccountId` 改为可省。省了由中转按自己的顺序选一家：判据与账户列表同一处（未损坏、官方登录、未导出、检测已通过），顺序照上游列表。会话视图里的 `officialAccountId` 告诉 MyCoAI 选了哪一家（只用于显示和「一台电脑同一时刻只绑一个账户」的核对）。
- 后端换账户时（管理员停用、删除、导出，或以后的自动切换），这个账户上的会话以新原因码 `account-switched` 结束，读句「中转换了官方账户，思域会自动重新连接。」，带 `remedy.action = recreate`。
- 普通上游（API 密钥那一类）也走思域，不在这份草案里：思域的代理只通到两个官方主机，正文原样发给官方；要发给第三方上游是另一套设计，待两边总监对齐。

## 6. 哪些原因码可以自动处理

| 原因码 | 自动处理 | 说明 |
| --- | --- | --- |
| identity-version-stale | recreate，带 `currentIdentityVersion` | 今天已有雏形，补上 `remedy` |
| profile-changed | recreate | 新回应里是新资料 |
| lease-expired、service-stopping、stopped-by-administrator | recreate | 只新开一轮；结果不明的请求不重放 |
| site-maintenance | wait，30 秒 | 维护完成后建会话会拿到新的身份代次 |
| access-database-unavailable、usage-unavailable | wait，30 秒 | 中转自己会好的情形 |
| account-switched（版本 2 新增） | recreate | 见第 5 节 |
| official-account-unavailable、official-account-gone | 版本 1：不自动，要人改选账户；版本 2：recreate，不带 `officialAccountId` | |
| device-mismatch、account-mismatch、identity-unsupported | 今天：只拒绝这一次，会话不结束。草案：中转同时以新原因码 `identity-mismatch` 结束会话，MyCoAI 下一次续租（最晚 40 秒）收到后 recreate、重写文件，只自动 1 次 | 身份文件被别的程序改了、或用户设置让客户端改走了 API 密钥，都会到这里；第二次还不符要人看 |
| 其余（personal-token-invalid、official-reauthentication-required、official-expiry-unknown、version-unsupported、protocol-unsupported、too-many-sessions、siyu-disabled、profile-* 、request-*、格式码） | 不自动 | 读出读句，等人 |

## 7. 「后端换了账户，客户端自动接着走」的两种情形

- **新开一轮**：旧会话结束 → MyCoAI 先停掉所有用默认位置的 Claude Code 进程（PROTOCOL.md 第 4.3 节第 1 条）→ 建会话、写三样 → 起新进程，从新对话开始。这条只用到今天已有的能力加上第 3 节的 `remedy`，可以先做。
- **原会话续接**：同一份对话记录（`--resume`）换到另一个官方账户接着发。能不能接，取决于 MyCoAI 第 4 批「续接与签名实验」（对话里已有的思考块签名、缓存在换了账户后是否仍被接受）。实验出结论前只做「新开一轮」；结论是能接，再把续接写进协议文本。

## 8. 甲、乙两条路的比较（2026-11-24 批示第 2 条）

- **甲：走打回**。拒绝告诉 MyCoAI 重建会话，新回应里是新账户的 `oauthAccount` 与身份代次，MyCoAI 写进 `.claude.json` 后重发。客户端此后生成的正文账户段就是新账户，与官方凭据属于同一个账户，和原生用户换账户登录一样。中转不改正文，守订单 152。可靠性取决于三件事：换文件前进程全部退出（否则会出 PROTOCOL.md 第 4.3 节第 1 条的两种事）、重试上限、留痕。
- **乙：中转放宽账户段核对**（认「一池可用账户」）。中转不改正文（订单 152），那么发给官方的正文账户段是旧账户 A，凭据却是新账户 B：官方看到正文自报的账户与凭据的主人不是同一个，原生客户端从来不会这样发。这违背 D-013「每个值取自真实的 Claude Code」，也让官方看到异常的客户端，属于 D-009 要管的设备与客户端风险。乙的另一种读法「按会话当时绑定的那一个」就是今天的做法：会话绑定在建会话时固定，运行中不换号，所以它并没有回答「换账户」。
- **建议：走甲，不走乙。**

## 9. 实现时要改的地方（对齐后另开单，现在不改）

- 中转：拒绝体加 `remedy`（`SiyuRefusal`、`SiyuControl.WriteRefusalAsync`）；结束原因加 `account-switched`、`identity-mismatch`（`SiyuCloseReason`、`SiyuCloseReasons`）；身份不符时结束会话（`SiyuSessionService.VerifyBody`）；打回留痕；版本 2 建会话可省账户（`SiyuCreateRequest`、`SiyuSessionService.CreateAsync`）。
- MyCoAI：照第 3、4、6 节处理 `remedy`，PROTOCOL.md 第 4.3 节的进程退出与设置冲突检查先于一切写文件。
- 契约用例：两边各写一组，照这份草案对齐后的正式文本：每个可自动处理的原因码一条正向（照做后重建成功）、白名单外的键一条负向（停下不照做）、到上限一条负向（停下并读出次数）、代理通道上的拒绝一条负向（不当指令）。
