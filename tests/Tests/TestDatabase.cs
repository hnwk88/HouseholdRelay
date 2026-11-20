using Application.Interfaces.Configuration;
using Application.Services.Configuration;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

/// <summary>
/// 测试专用的库操作：布置触发器、直接改行、读回站点身份。运行时已经没有执行任意 SQL 的入口（所有数据经通用仓储），这些只给测试制造故障与核对结果用。
/// </summary>
internal static class TestDatabase
{
    /// <summary>打开一个新连接并开启事务执行 <paramref name="action"/>，正常返回才提交；与改造前的 <c>RelayDatabase.Write</c> 同一语义。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="database">测试实例的数据库。</param>
    /// <param name="action">在事务里执行的操作。</param>
    public static T Write<T>(this RelayDatabase database, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>取容器里的站点身份服务（具体类型），用它的内部存取方法核对库里的持久身份。</summary>
    /// <param name="services">测试实例的容器。</param>
    public static SiteIdentityService SiteIdentityStore(this IServiceProvider services) => (SiteIdentityService)services.GetRequiredService<ISiteIdentityService>();
}
