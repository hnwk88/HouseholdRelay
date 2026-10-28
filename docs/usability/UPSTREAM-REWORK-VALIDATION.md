# 上游兼容性整改与真实验证

日期：2026-10-28。基线：94826c5。已修复第一家上游网页检测阻止激活，以及中转替换 User-Agent 导致原生请求被拒的两处问题。完成本轮工程师验证，交回总监复验；这里不宣告整体验收通过。

## 原因与请求证据

同一 `https://aitoll.net/v1/cc`、API/Bearer、`claude-opus-5-5`、`http://127.0.0.1:7897`，保留前缀 `/v1/cc`；出站 TLS 正常验证且不跟随跳转。先将隔离原生 Claude Code 2.1.311 的专用短提示请求保存在内存，再逐项改变非秘密特征。没有保存真实请求正文、真实令牌或用户业务内容。

| 受控请求 | 实际结果 |
| --- | --- |
| 原固定 16 Token 非流式探测 | 400，client_invalid_request / client_request_rejected |
| 只换原生 User-Agent、只补原生请求头、只提高到 1024 Token | 均为 400 |
| 原生请求的完整消息结构和原生请求头 | SSE 200，含 message_stop |
| 原生请求仅改为 stream=false | JSON 200，真实 message/assistant 和 usage |
| 原生请求仅把 User-Agent 改为 HouseholdRelay/1.0 | 400 |
| 原生请求仅移除 system，或换为通用助手 system | 400 |
| 原生请求仅移除 metadata | SSE 200，含 message_stop |
| 简短标准请求改为流式 | 400 |

这些反例证明拒绝条件至少涉及客户端标识和原生系统消息，不能归因于 Bearer 无效、16 Token 太小、或只支持流式。没有上游服务端代码，不能声称已还原它的全部校验规则，也不能把本次结果推广为通用 Messages 客户端均可用。

完整安全结果在 `artifacts/upstream-rework/request-comparison.json`、`request-body-comparison.json`、`request-restriction-comparison.json`；归档副本为 [请求差异](evidence/upstream-request-differences.json)。保留状态、必要错误标识、请求标识、模型用量和结构字段名，不含正文或秘密。

## 实现与取舍

- 标准 Messages 仍为默认检测，不调用本机 Claude Code。增加显式的“Claude Code 实际调用”选项，随地址、认证、模型及凭据保存到同一候选；原有数据库缺少该字段时使用标准检测。
- 特定上游使用真实安装的原生客户端生成请求并完成会话，不硬编码客户端版本、系统提示或签名。客户端运行在独立受 ACL 保护的目录，禁用工具、会话持久化、外部 MCP 与浏览器功能，清除继承的账户/代理配置；只发“Reply with OK.”。
- 客户端只拿到临时本机诊断令牌。专用回环入口只把第一条 Messages 请求发往固定候选，通过原 UpstreamConnection 使用候选的凭据、代理和 TLS 策略；客户端后续重试不能再产生上游生成请求。诊断总超时仍为 30 秒。
- 原生诊断检查真实 SSE 的 Messages 结构、文本生成、结束原因及 message_stop，并要求客户端成功退出；假 200、只有结束事件、中断或 error 均不能激活。整个诊断响应最多 1 MB，普通业务 SSE 继续逐事件刷新。
- API 模式保留客户端有效 User-Agent；缺失、过长、含控制字符或被 Connection 指定为逐跳头时回退到部署设置。账户模式仍保持统一部署身份。认证头始终按同一上游快照替换，API 不发送 OAuth beta。
- 网页明确显示检测方式；连接说明注明原生检测只证明 Claude Code 场景，curl 示例仅说明认证写法。第一家的标准请求依然可能被上游拒绝，这不是本项目承诺支持的通用请求转换。

已核对 MyCoAI 提交 `3f3f3a27ed05f0229d9eb94521bd9cfcee86cb67` 的目录认证识别、AddApiAccountViewModel 回填、AccountManagementService 保存、EnvironmentVariableClaudeCodeProcessLauncher 最终环境变量使用。本项目保留手动认证选择，与地址和 DPAPI 凭据共同持久化；不新增自动认证探测或生成重试，没有修改 MyCoAI 工作账户。

## 真实网页与客户端结果

使用发布产物、无窗口 Edge 156.0.4314.50、隔离 Claude Code 2.1.311。管理员在全新临时实例的网页完成首次设置、创建一个使用者、录入上游凭据、测试和激活。同一个个人令牌贯穿两家及重启；凭据来自前次项目专用预检目录的 DPAPI 配置，仅只读解密后经进程管道传给浏览器，不写入命令行或日志。

| 上游 | 配置 | 网页检测/激活 | JSON / SSE | 原生会话与工具 | 重启 |
| --- | --- | --- | --- | --- | --- |
| https://aitoll.net/v1/cc | API，Bearer，Claude Code 实际调用 | 通过 | 200 / 200，正常结束 | 简短会话成功；Read + tool_result 两回合成功 | 同一令牌 JSON 200 |
| https://api.sftoken.top | API，x-api-key，标准 Messages | 通过 | 200 / 200，正常结束 | 简短会话成功；Read + tool_result 两回合成功 | 同一令牌 JSON 200 |

模型均为 `claude-opus-5-5`，出站代理均为 `http://127.0.0.1:7897`。从第二家再次切回第一家，网页重新测试/激活后原生会话成功。390px 窄屏无水平溢出；两家的检测、连接说明及窄屏共五张截图均已目视检查，秘密字段为空。临时服务已全部停止。

JSON/SSE 验证使用原生客户端本次在内存中生成的专用测试请求；JSON 只改变 stream 标志。没有把第一家的原生格式成功写成任意简短 curl 请求成功。真实客户端下游只有个人令牌，上游凭据由项目替换。工具仅读取临时目录中的 probe.txt 固定标记，没有读取工程文件或用户业务资料。

原始结构化结果及各次模型用量：`artifacts/upstream-rework/real-upstream-results.json`，可提交归档见 [真实调用结果](evidence/upstream-real-results.json)。网页诊断另有少量消耗；客户端价格估算不等同于供应商账单。

## 回归与复现

Release 构建通过，0 警告/0 错误，最终完整回归 39/39 通过。原有 36 项全部保留，新增受限上游的实际客户端检测/认证/前缀/激活/转发/重启/拒绝不重放、不完整或错误 SSE 不得通过，以及原生诊断取消后释放进程与连接的检查。最终输出见 `artifacts/upstream-rework/build.log`、`tests.log`、`publish.log`。

取消补测曾发现：取消信号使客户端退出等待先完成，可能被误判为客户端启动失败。现先检查取消状态，返回“检测已取消”；连接取消、子进程退出、活动版本不变及无重放均已由该用例验证。

本机回归不调用真实模型：

```powershell
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release --no-build
dotnet run --project HouseholdRelay.Tests -c Release --no-build -- 'restricted upstream'
```

受限上游用例先让原标准请求返回 400，确认只发送一次且不能激活；显式原生检测收到真实模拟 SSE 后才能激活；再把原生请求通过个人令牌送入本项目，验证客户端标识、请求正文、路径与认证隔离。撤销 API User-Agent 保留改动会使这个中转步骤失败。原生检测遇到 400 也只发送一次，活动配置版本不受影响。

真实验证会消耗额度，仅显式执行下列入口时运行，默认测试集不会调用它。两份预检 JSON 须仍指向本机同一 Windows 身份可解密的项目专用目录：

```powershell
dotnet publish HouseholdRelay -c Release --no-build --self-contained false -o artifacts/upstream-rework/publish
$env:NODE_PATH = Join-Path $env:TEMP 'HouseholdRelay-browser-tools/node_modules'
dotnet HouseholdRelay.Tests/bin/Release/net10.0-windows/HouseholdRelay.Tests.dll --real-upstream-browser artifacts/upstream-compatibility-20261028/relay-opus-proxy-preflight.json artifacts/upstream-compatibility-20261028/sftoken-api-key-relay.json artifacts/upstream-rework/publish/HouseholdRelay.dll artifacts/upstream-rework
```

手动配置第一家：API 密钥 → 兼容 Anthropic Messages → 根地址 `https://aitoll.net/v1/cc` → Bearer → `claude-opus-5-5` → Claude Code 实际调用 → 独立填写出站代理 → 保存候选 → 确认额度并测试 → 激活。第二家使用 `https://api.sftoken.top`、x-api-key、标准 Messages，其余流程相同。

## 仍未关闭的验收范围

两家都是中转 API 凭据，不能充当真实账户 OAuth 材料。真实账户令牌失效更新、可信公网 HTTPS 的两字段接入及原任务书其他未覆盖项目仍需总监逐项复验。本轮的下游管理和客户端地址为本机 HTTP，只有出站上游是正常证书校验的 HTTPS；没有注入私有 CA 或把本机 HTTP 结果写成公网验证。实际原生客户端依赖仅适用于显式选择该检测的上游，普通 API 接入无此安装前置。
