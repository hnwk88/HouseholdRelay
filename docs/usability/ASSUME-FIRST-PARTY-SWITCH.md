# 让家里的 Claude Code 按官方直连的方式发请求：一个开关的配置方法

适用于经中转用官方账户的每台家里电脑。只需要配一次（方法二或方法三），或者每次照连接页复制（方法一）。

## 这个开关是什么

开关名：`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`，值：`1`。

Claude Code 连自定义地址（就是中转）时，默认会少发一批只给官方直连的东西：4 个 beta、正文里的 diagnostics、5 个网关提示头、计费头里的扩展段。开了这个开关，Claude Code 把中转当成官方地址，这些值都由它自己当场生成，和原生直连一致，中转原样转给官方。

实测依据（订单 133 第二段，Claude Code 2.1.323，中转机回环、不外连）：打开后不多出任何连接，经中转只有对话和探活两种请求，一问、接着问、调工具、流式中断都和不开时一样。

注意：这是 Claude Code 内部开关，名字以下划线开头，官方没有写进文档。Claude Code 升级后如果它改名或失效，请求只是退回不开时的样子，不会出错；中转这边每次升级会重新核对。

## 方法一：每次开终端时复制（现在连接页给的方式）

打开中转「连接说明」页，「Claude Code（PowerShell）」那一段有三行，按「复制三个变量」，粘贴到要运行 Claude Code 的 PowerShell 窗口，回车，再运行 `claude`：

```powershell
$env:ANTHROPIC_BASE_URL = 'https://www.zkmryl.com:8787'
$env:ANTHROPIC_AUTH_TOKEN = '<你的个人中转令牌>'
$env:_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL = '1'
```

只对这个窗口有效，关掉窗口就没了。

## 方法二：给这台电脑的这个 Windows 用户永久设上（推荐，一次就好）

在 PowerShell 里执行一行（不需要管理员）：

```powershell
[Environment]::SetEnvironmentVariable('_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL', '1', 'User')
```

然后把所有终端、编辑器和 Claude Code 完全关掉再打开。以后这个用户启动的 Claude Code 都会带上它。

不想敲命令的话，用系统设置也一样：开始菜单搜「编辑账户的环境变量」→ 上半部分「某某的用户变量」→ 新建 → 变量名填 `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`，变量值填 `1` → 确定。

要撤掉：

```powershell
[Environment]::SetEnvironmentVariable('_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL', $null, 'User')
```

## 方法三：只写进 Claude Code 自己的设置文件（只影响 Claude Code）

打开 `%USERPROFILE%\.claude\settings.json`（设了 `CLAUDE_CONFIG_DIR` 的话，是那个目录里的 `settings.json`）。没有这个文件就新建，内容是：

```json
{
  "env": {
    "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1"
  }
}
```

文件已经有内容时，不要整份覆盖：如果已有 `"env": { … }`，只在里面加一行 `"_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1"`；没有 `env` 就在最外层大括号里加上整段 `"env"`。改完重开 Claude Code。

这种写法也在中转机回环上实测过：只写这个文件、不设环境变量，Claude Code 照样补齐了 diagnostics、x-client-request-id 和 cache-diagnosis beta。

## 三种方法怎么选

| 方法 | 要做几次 | 影响范围 |
|---|---|---|
| 一：连接页复制 | 每开一个终端一次 | 只有这个窗口 |
| 二：用户环境变量 | 每台电脑每个用户一次 | 这个用户启动的所有程序 |
| 三：settings.json | 每台电脑每个用户一次 | 只有 Claude Code |

这台电脑上的人如果也直接用官方账户登录 Claude Code（不经中转），开关对它没有影响：直连本来就算官方地址。
如果同一个用户还用 Claude Code 连别的第三方地址，那边也会收到这些额外字段；第三方按原样转发，是否接受由第三方决定。
