using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Persistence;

/// <summary>
/// EF Core 每次开事务前经这里：SQLite 开写事务（BEGIN IMMEDIATE）时等写锁的上限取的是连接上的默认等待，不是命令超时，
/// 所以把它对齐到本上下文此刻的命令超时（通用仓储 <c>WithLockWaitAsync</c> 设的上限）；没设时回到连接串的默认值（10 秒）。
/// 这样「用量写入只等 1 秒」「站点身份换号写锁被占就立刻失败」在开事务这一步也成立。
/// </summary>
internal sealed class RelayTransactionInterceptor(int defaultTimeoutSeconds) : DbTransactionInterceptor
{
    /// <summary>开事务前对齐等锁上限。</summary>
    /// <param name="connection">将要开事务的连接。</param>
    /// <param name="eventData">事件数据（带上下文）。</param>
    /// <param name="result">拦截结果。</param>
    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Align(connection, eventData.Context);
        return result;
    }

    /// <summary>开事务前对齐等锁上限。</summary>
    /// <param name="connection">将要开事务的连接。</param>
    /// <param name="eventData">事件数据（带上下文）。</param>
    /// <param name="result">拦截结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        Align(connection, eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>把连接的默认等待设成上下文的命令超时，没有就用连接串默认值。</summary>
    private void Align(DbConnection connection, DbContext? context)
    {
        if (connection is SqliteConnection sqlite && sqlite.State == ConnectionState.Open)
            sqlite.DefaultTimeout = context?.Database.GetCommandTimeout() ?? defaultTimeoutSeconds;
    }
}
