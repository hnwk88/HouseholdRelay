# 订单 61 验收不通过与整改要求

日期：2026-10-28。验收人：总监。被验收提交：`94826c5cbdede888fc1c8cb0435a34ff6c02bf6b`。

## 判定

验收不通过，退回本单整改。第一家真实上游已经由用户在 MyCoAI 正常使用，总监也已独立复现原生客户端调用成功；当前 HouseholdRelay 的管理检测却阻止该配置激活，用户无法完成网页接入到实际调用的完整旅程。这违反本单兼容中转直接配置、真实调用检测及候选测试激活的交付要求。

总监此前只追加了预检进度，没有进入验收并作判定，工程师因此不能合法开始整改。这一流程遗漏由总监承担。本次先核对订单列表和完整订单，再将 61 从“待验收”转为“验收中”，补齐独立验证和本验收结论后转为“待整改”。62、63 仍留在“待派单”。

## 阻断项与复现证据

1. 第一家根地址 `https://aitoll.net/v1/cc`，接入模式 API，认证方式 Bearer（Authorization 头），模型 `claude-opus-5-5`，出站代理 `http://127.0.0.1:7897`。保留 `/v1/cc` 前缀，模型和生成接口分别为 `/v1/cc/v1/models`、`/v1/cc/v1/messages`。
2. 使用订单交付的发布程序和独立临时目录，在网页保存候选并测试：连通/TLS、鉴权、模型目录通过，最小模型调用返回 HTTP 400，候选不能激活。直接发送相同的最小 Messages 请求也得到 `client_invalid_request / client_request_rejected`。
3. 同一地址、凭据、模型和代理，通过隔离配置目录中的原生 Claude Code 2.1.311 调用，返回 `OK`，退出码 0。这证明真实业务接入材料可用；具体导致最小请求被拒的字段或行为差异尚未定位。
4. 上述 400 已发生在正确使用 Bearer 且目录成功之后。整改必须继续定位生成检测与真实客户端请求的差异，不能仅增加 Bearer 选项后就宣布解决。

完整结果已经逐份读取，位于 `artifacts/upstream-compatibility-20261028/relay-opus-proxy-preflight.json` 和 `aitoll-native-client.json`；过程与参考在 `docs/usability/UPSTREAM-PREFLIGHT-20261028.md`。

第二家作为正向对照：根地址 `https://api.sftoken.top`，API 模式、x-api-key、相同模型和代理；目录、网页候选检测和激活、经本项目个人令牌转发的 JSON/SSE 均成功。证据为 `sftoken-api-key-models.json`、`sftoken-api-key-relay.json`。后续修复须保持这个结果。

## 必须整改

1. 从 MyCoAI 已修好的完整接入链路和原生客户端的真实请求出发，定位第一家最小检测被拒的原因，修复从网页配置、测试、激活到实际客户端调用的兼容性。请求比较只记录非秘密差异，不保存真实令牌或用户业务正文。
2. 核对 MyCoAI 提交 `3f3f3a27ed05f0229d9eb94521bd9cfcee86cb67`。参考 `AccountModelCatalogClient` 的认证识别、`AddApiAccountViewModel` 的组合框回填、`AccountManagementService` 的保存、`EnvironmentVariableClaudeCodeProcessLauncher` 的最终使用；认证方式须与地址和凭据作为同一配置保存。第一家使用 Bearer，第二家使用 x-api-key，两家都走配置的出站代理。
3. 本项目已有手动认证选择，原任务书允许该方式。本次必须修复的是实际接入阻断；自动识别若作为易用性改进采用，应限定于目录探测及明确认证拒绝，不扩展为生成请求自动重放。
4. 保留真实调用成功才标记检测成功的要求。不能将目录 HTTP 200、假回复、删除检测、修改数据库激活标记或关闭 TLS 校验当成修复；API 模式仍不以安装 Claude Code 作为一般前置。若发现必须重新评估的上游能力限制，交付可复核的请求证据与适用范围，不能自行宣布通过。
5. 补上能够复现本次真实差异的回归测试，并提供两家分别从网页接入到本项目转发调用的证据。覆盖非流式、流式、实际 Claude Code 会话/工具、配置切换与重启后调用；记录客户端版本、模型、认证方式和安全的请求结果。
6. 更新交付文档中“没有真实 API 上游材料”的旧表述。用户已提供真实材料，预检已验证可用；凭据可沿用本次项目专用预检目录中的 DPAPI 配置，目录位置见对应结果 JSON，不写入任务书、源码或日志，不改动用户正常工作的 MyCoAI 账户。

## 本次独立验证

- 总监在正式进入“验收中”后运行 `dotnet run --project HouseholdRelay.Tests -c Release --no-build`，完整输出逐项读取，36/36 通过。日志：`artifacts/acceptance-order-61/tests.log`。这些用例调用本机模拟服务，不能覆盖已经复现的真实上游阻断。
- 从同一提交导出独立临时源码副本，抽取响应秘密脱敏作受控反例。先只改坏原文替换时，URL 编码替换对该夹具仍等价生效，用例 1/1 通过，未造成预期的行为破坏；这次未检出的运行保留在 `mutation-result.json` 和对应日志中。
- 随后在同一隔离副本同时使原文与 URL 编码替换失效，构建成功，0 警告、0 错误；`upstream JSON and SSE` 用例在秘密不得泄露断言处失败，0/1、退出码 1，反例有效。完整输出：`mutation-complete-build.log`、`mutation-complete-tests.log`、`mutation-complete-result.json`。工程工作区源码未改动。

## 再交付与未关闭项目

工程师从“待整改”认领并进入“整改中”，完成修复、真实联调、必要回归、完整结果和提交后交回“待验收”，由总监复验。

自建公网可信 HTTPS、真实账户令牌更新及其余原任务书验收项没有因本次正向对照而自动通过；本次不通过的直接依据是已复现的第一家接入阻断。两枚中转 API 凭据不能自动充当真实账户 OAuth 证据，缺少的部署条件应如实保留。后续订单仍遵守前置验收顺序。
