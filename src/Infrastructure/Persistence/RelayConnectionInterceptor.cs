using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Persistence;

/// <summary>
/// EF Core 每次打开连接都经这里：打开前核对数据库文件与实例标记仍在、标记里的编号仍是本实例，打开后设完全同步并核对库里的实例编号。
/// 核对与迁移链的 <see cref="RelayDatabase.Open"/> 用同一段代码，任何一项不对都抛出，调用方拿不到连接。
/// </summary>
internal sealed class RelayConnectionInterceptor(RelayDatabase database) : DbConnectionInterceptor
{
    /// <summary>打开前核对文件与实例标记。</summary>
    /// <param name="connection">将要打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    /// <param name="result">拦截结果。</param>
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        database.EnsureAvailable();
        return result;
    }

    /// <summary>打开前核对文件与实例标记。</summary>
    /// <param name="connection">将要打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    /// <param name="result">拦截结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        database.EnsureAvailable();
        return ValueTask.FromResult(result);
    }

    /// <summary>打开后设完全同步并核对库里的实例编号。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => database.Verify((SqliteConnection)connection);

    /// <summary>打开后设完全同步并核对库里的实例编号。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        database.Verify((SqliteConnection)connection);
        return Task.CompletedTask;
    }
}
