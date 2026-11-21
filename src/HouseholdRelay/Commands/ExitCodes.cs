namespace HouseholdRelay.Commands;

/// <summary>
/// 命令行的退出码，按失败类别固定：脚本只看退出码就知道该找谁。参数错误和业务拒绝沿用一直以来的 1，
/// 数据库、本机环境（文件、Windows 凭据、端口、加解密）和未预料的失败各有自己的码。client 子命令把 Claude Code 自己的退出码原样交回，不在这里。
/// </summary>
public static class ExitCodes
{
    /// <summary>成功。</summary>
    public const int Success = 0;

    /// <summary>参数错误或业务拒绝（未知子命令、端口不合法、操作被拒等），提示已写到标准错误。</summary>
    public const int Rejected = 1;

    /// <summary>数据库操作失败（SQLite 错误）。</summary>
    public const int Database = 2;

    /// <summary>本机环境失败：文件、Windows 凭据、端口或加解密。</summary>
    public const int Environment = 3;

    /// <summary>未预料的失败；不打印消息与堆栈。</summary>
    public const int Unexpected = 4;
}
