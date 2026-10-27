# Household Relay — C# 单账号中转

**书房、卧室、公司各用自己的 Claude Code；通过花生壳连接家中同一个中转主机。** 代码和工具执行保留在各自电脑上，账号凭据只保存在中转主机。

纯 C# / .NET 10 / ASP.NET Core，无第三方 NuGet 包，无数据库。提供控制台程序和可直接打开的 Visual Studio 解决方案。运行发布包需要 **ASP.NET Core Runtime 10 x64**（安装 .NET 10 SDK 也可以）。公司电脑无需 VPN。

```text
书房客户端 ── study 令牌 ────┐
卧室客户端 ── bedroom 令牌 ──┼─ HTTPS → 花生壳公网 TCP 5433
公司客户端 ── office 令牌 ───┘                    ↓ TCP 透传
                                      家中主机 TCP 8787
                                                ↓
                                  单一账号 / 设备 ID / 连接池
                                                ↓
                                           Claude API
```

## 第一次使用

### 1. 花生壳配置

使用 **TCP 映射**，公网端口 `5433`，内网目标为中转主机 IP，内网端口 `8787`。如果花生壳客户端就运行在中转主机上，目标可以设为 `127.0.0.1:8787`。这里由中转程序负责 HTTPS，花生壳只转发 TCP 字节，不使用会缓冲流式回复的 HTTP 页面代理。

确认完整访问域名，例如 `your-host.example.com`。下面命令中的域名是占位符，必须换成自己的。只知道端口 `5433` 还不能确定访问地址。

主机需保持开机、联网。程序不自动修改花生壳配置、路由器、防火墙或全局代理。

### 2. 在中转主机初始化

解压发布包到可写目录。在这个目录打开 PowerShell：

```powershell
.\HouseholdRelay.exe init --host your-host.example.com --public-port 5433 --listen-port 8787
claude setup-token
.\HouseholdRelay.exe account
.\HouseholdRelay.exe serve
```

- 主机需安装官方 Claude Code；初始化会读取其版本，确定固定的上游 User-Agent。
- `init` 生成 HTTPS 证书和三个独立客户端包，不读取现有 Claude 登录凭据。
- `claude setup-token` 使用官方交互登录流程生成上游令牌。
- `account` 隐藏输入令牌，并用当前 Windows 用户的 DPAPI 加密保存。不要把令牌作为命令行参数。
- `serve` 启动中转，保持这个窗口运行，Ctrl+C 停止。

默认数据保存在程序旁边的 `.state`。主机命令也可加 `--home D:\RelayData`，指定数据目录；后续命令使用同一个目录。

### 3. 分发客户端包

初始化后会生成：

| 目录 | 使用电脑 |
| --- | --- |
| `clients/study` | 书房 |
| `clients/bedroom` | 卧室 |
| `clients/office` | 公司 |

把对应的整个目录复制到对应电脑，例如 `C:\Tools\Relay`。包里有程序、该设备的独立令牌和公开 CA 证书；**没有上游账号令牌或服务器私钥**。不要复制主机的 `.state` 目录。

每台电脑需要 .NET 10 ASP.NET Core 运行时，以及官方 Claude Code。打开**自己的项目目录**，启动：

```powershell
C:\Tools\Relay\HouseholdRelay.exe client
```

需要传 Claude Code 参数时：

```powershell
C:\Tools\Relay\HouseholdRelay.exe client -- --model sonnet
```

也可以用同一份主程序指定客户端包：

```powershell
.\HouseholdRelay.exe client --bundle .\clients\study
```

启动器只修改 Claude Code 子进程的环境变量；使用客户端包中的 `.claude-state` 保存独立配置和历史，不改现有全局登录文件。原有用户级插件和偏好不会自动复制到这个独立配置目录。项目自己的设置仍需避免覆盖认证、apiKeyHelper 或提供商。

## 怎样集中成一个主机的请求特征

| 部分 | 实现 |
| --- | --- |
| 上游网络连接 | 所有模型 API 请求由家中同一个 C# 进程发出，复用同一个 `SocketsHttpHandler` 连接池 |
| 账号凭据 | 一个主机令牌，客户端只持有各自的中转令牌 |
| User-Agent | 初始化时读取主机 Claude Code 版本，生成固定值；不同客户端不会随意改变它 |
| SDK 与转发诊断头 | 去除 `x-stainless-*`、`Forwarded`、`X-Forwarded-*` 等客户端传输诊断信息 |
| 设备元数据 | 为主机生成并持久保存 64 位十六进制设备 ID，只替换已识别 `metadata.user_id` 格式的设备字段 |
| 会话信息 | 保留每个 session ID 和 account ID，不合并会话、不复用别人的工具结果 |
| 提示词、代码和工具 | 不修改内容，不修改系统提示里的 attribution 字符串；元数据补丁采用局部字节替换 |

已支持旧的 `user_<device>_account_<account>_session_<session>` 格式，以及包含 `device_id` 的 JSON 字符串格式。未知格式原样转发；可以在 `.state/server.json` 中把 `normalizeDeviceMetadata` 改为 `false`。两端保持相同 Claude Code 版本有助于协议一致性；升级后可修改 `upstreamUserAgent` 并重启。

**这只统一程序可控制的传输和设备元数据，不代表所有请求都能被认定为同一台物理电脑。** 本地项目路径、操作系统提示和会话内容仍可能不同；MCP、WebFetch、浏览器、云任务等额外网络活动也不全部经过这个模型 API 中转。设备归一处理不是账号可用性的证明。

## 访问控制和可靠性

- 每客户端一个 256 位随机访问令牌，服务端只存哈希，固定时间比较；分别停用或轮换。
- 默认账号全局并发 2，每客户端并发 1；队列上限 16，排队超时 30 秒。
- 默认每客户端每分钟 120 次请求，上传上限 16 MiB，上传超时 30 秒，上游请求超时 10 分钟。
- SSE 流式转发，收到数据即推送；取消客户端请求会取消对应的上游连接。
- 429 触发全局冷却，遵守 Retry-After；401/403 暂停后续上游请求，更新凭据并重启后恢复。
- 中转不自动重放请求，不跟随上游重定向，不把流式回复断点拼接成新回复。Claude Code 自身仍有重试机制。
- 上游固定为 `https://api.anthropic.com`，仅开放 Messages、Count Tokens、Models 路由，不是通用公网代理。
- HTTPS 完整校验证书；只为 Claude Code 子进程加载私有 CA，不关闭校验、不安装系统根证书。
- 主账号令牌与服务器私钥使用 DPAPI 加密；状态目录限制当前用户和 SYSTEM。运行时 Windows TLS 使用临时用户密钥容器，正常释放时由框架清理。当前用户下的其他程序或管理员仍可访问运行中的进程。
- 日志仅含请求标识、客户端名称、固定路由、状态和耗时，不记录提示词、代码、令牌。

客户端包中的中转令牌是可转移的明文凭据，需要妥善保管；丢失后在主机撤销对应令牌。

## 日常管理

在主机上：

```powershell
.\HouseholdRelay.exe clients list
.\HouseholdRelay.exe clients disable --name office
.\HouseholdRelay.exe clients enable --name office
.\HouseholdRelay.exe clients rotate --name bedroom
.\HouseholdRelay.exe clients add --name laptop
```

客户端访问名单约 0.5 秒内热更新。停用或轮换会阻止后续请求及排队请求，已经发出的请求可以完成。轮换后重新复制对应客户端包；不要并行运行多个管理命令。最多 8 个客户端令牌，始终只有一个上游账号。

上游令牌更新：重新执行官方 `claude setup-token`，再运行 `account`，重启 `serve`。程序不自动读取或刷新官方客户端的登录凭据。

配置里的并发、超时、设备归一、User-Agent 和出站代理修改后需要重启。主机如需使用本地 HTTP 代理，在 `server.json` 中设置：

```json
"upstreamProxy": "http://127.0.0.1:7890"
```

不填则直接出站。代理仅作用于主机连接上游，客户端访问花生壳域名不依赖 VPN。

## 故障定位

- **连接失败**：核对花生壳域名、5433 是否为 TCP 映射、目标主机与 8787 端口、主机是否开机。花生壳接收端在同一主机时，可用本机回环地址避免不必要的局域网开放。
- **证书错误**：URL 域名必须与初始化时一致，并完整复制 `relay-ca.pem`，通过 `client` 命令启动。证书有效期 2 年。更换域名或证书时，需要重新初始化到新数据目录并重新分发客户端包。
- **中转 401**：该客户端令牌不正确或已停用。
- **上游 401/403，随后 503**：检查第一条上游错误，确认令牌及协议兼容性；更新主机令牌并重启。
- **429**：等待冷却或排队结束，不要反复重启规避服务器限流。
- **502**：检查主机出站网络及上游代理配置。程序不会自动切换其他账号。
- **更换 Windows 用户/重装系统**：原 DPAPI 文件不能直接移植，需要重新设置凭据及证书。

`GET /healthz` 只证明中转可达；带客户端令牌的 `GET /status` 可查看并发和暂停状态。只有实际模型请求成功，才能证明真实上游账号已经接通。

## 开发、构建与验证

打开仓库根目录的 `HouseholdRelay.sln`，或使用：

```powershell
dotnet build HouseholdRelay.sln -c Release
dotnet run --project HouseholdRelay.Tests -c Release
dotnet publish HouseholdRelay -c Release --self-contained false -o artifacts/HouseholdRelay-release
```

测试工程不依赖测试框架包，使用本地模拟上游验证协议、并发、限流、取消、加密与客户端启动。最后一项会启动本机安装的两个真实 Claude Code 进程，经 HTTPS 和模拟 TCP 端口映射调用本地上游；使用隔离配置和虚构令牌，不发送真实账号的模型请求。

本次本地验证不能替代真实 OAuth 接入验证；真实账号和花生壳公网地址尚需部署后联调。支持范围为终端 Claude Code 的模型 API 调用，不包括 Claude 网页、Remote Control 或云端任务接口。
