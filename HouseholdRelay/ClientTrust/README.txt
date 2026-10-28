HouseholdRelay 私人设备一次性信任（Windows 当前用户）

此包只含 CA 公共证书、安装脚本和说明，不含上游凭据、个人令牌或私钥。
仅为你自己的受控中转主机安装信任。先向管理员独立核对连接页上的 CA SHA-256 指纹。

1. 解压到本机文件夹，在 PowerShell 中进入该目录。
2. 阅读 Install-RelayTrust.ps1。若系统将下载文件标为来自互联网，核对文件来源后在文件属性中解除锁定。
3. 执行（将占位符替换为管理员提供的 64 位指纹）：
   .\Install-RelayTrust.ps1 -ExpectedSha256 指纹
   脚本按当前 Windows 用户安装根证书，并设置用户级 NODE_EXTRA_CA_CERTS。
   Windows 可能要求确认安装根证书；脚本先核对 SHA-256，并显示系统窗口使用的 SHA-1 供再次核对。
   核对后确认，取消则不会完成安装。
   若本机执行策略不允许脚本，请按组织策略处理；不要关闭 TLS 校验。
4. 完全退出并重新打开终端、Claude Code 和浏览器；不要沿用安装前启动的终端父进程。
   如需在当前终端立即生效：
   $env:NODE_EXTRA_CA_CERTS = [Environment]::GetEnvironmentVariable('NODE_EXTRA_CA_CERTS', 'User')
5. 从管理员取得实际可达的 HTTPS 根地址及自己的个人令牌，按连接页设置两项连接变量并选择推荐模型。
6. 用浏览器打开中转根地址下的 /healthz，应无证书警告；再运行 Claude Code 的最小会话验证客户端信任。
   浏览器成功不代表 Claude Code 已继承新环境。不要设置 NODE_TLS_REJECT_UNAUTHORIZED=0 或使用 -k。

安装范围：只影响当前 Windows 用户；其他用户和其他电脑需各安装一次。
脚本保留原有 NODE_EXTRA_CA_CERTS 内容，在本机用户目录组合公共 CA；文件缺失时拒绝覆盖旧设置。
CA 更换后必须重新核对指纹并安装；地址变化也可能需要管理员重新签发匹配证书。
不需要管理员权限。公共 CA 可分发，服务端 .state、PFX、DPAPI 文件不可作为客户端包分发。

撤销信任：完全退出客户端，运行 certmgr.msc，在“受信任的根证书颁发机构”中按指纹删除本 CA。
安装记录在 %LOCALAPPDATA%\HouseholdRelay\trust\指纹\installation.json，记录原用户级 NODE_EXTRA_CA_CERTS。
如果当前用户变量仍指向这个安装目录，可恢复记录中的 previous_user_ca（空值则删除该变量）。
若后来又安装了其他 CA，应从当前组合文件中精确移除本 CA，保留其他信任。
