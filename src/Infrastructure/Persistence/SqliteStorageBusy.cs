using Application.Interfaces.Configuration;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Persistence;

/// <summary>SQLite 的「库正忙」：错误码（取低 8 位主码）是 SQLITE_BUSY（5）或 SQLITE_LOCKED（6）；经 EF 写库时 SQLite 异常包在 DbUpdateException 里，沿内层异常链找。</summary>
internal sealed class SqliteStorageBusy : IStorageBusy
{
    /// <summary>沿异常链找 SQLite 异常，主码是 5 或 6 就是库正忙。</summary>
    /// <param name="error">存取时抛出的异常。</param>
    public bool IsBusy(Exception error)
    {
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
            if (inner is SqliteException sqlite) return (sqlite.SqliteErrorCode & 0xff) is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED;
        return false;
    }
}
