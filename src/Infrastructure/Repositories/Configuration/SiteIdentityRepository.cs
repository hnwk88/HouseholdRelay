using Application.Interfaces.Configuration;
using Core.Configuration;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Configuration;

/// <summary>站点身份事务；只有 Begin 能生成维护新号，所有历史重试都只读已有结果。</summary>
internal sealed class SiteIdentityRepository(RelayDatabase database, IDeviceIdGenerator deviceIds) : ISiteIdentityRepository
{
    private readonly SemaphoreSlim writeGate = new(1, 1);

    /// <summary>在同一读事务取得 canonical 编号及对应代次，避免跨提交拼接快照。</summary>
    public SiteIdentitySnapshot Read()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadCurrent(connection, transaction);
    }

    /// <summary>在同一只读事务中取得历史结果与当前代次，不改变准入或写入任何记录。</summary>
    public SiteIdentityOperationResult? Find(string operationId)
    {
        operationId = CheckOperation(operationId);
        using var connection = database.Open(timeoutSeconds: 1);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadOperation(connection, transaction, operationId, ReadCurrent(connection, transaction));
    }

    /// <summary>排空之后调用；事务锁覆盖幂等检查和生成，竞争请求不会各自产生新号。</summary>
    public SiteIdentityOperationResult Begin(string operationId, long expectedVersion, CancellationToken cancellation = default)
    {
        operationId = CheckOperation(operationId);
        return WriteIdentity(cancellation, "站点配置正由另一项操作保存，设备编号未更换。请稍后继续原操作。", (connection, transaction) =>
        {
            var current = ReadCurrent(connection, transaction);
            if (ReadOperation(connection, transaction, operationId, current) is { } repeated) return repeated;
            if (current.Pending) throw new InvalidOperationException("已有设备维护尚未完成，请继续原操作。");
            if (current.Version != expectedVersion) throw new InvalidOperationException("站点身份版本已变化，请重新加载页面。");
            var settings = ReadSettings(connection, transaction);
            cancellation.ThrowIfCancellationRequested();
            var generated = deviceIds.Generate();
            if (!SiteDeviceIdentity.IsValid(generated) || StringComparer.Ordinal.Equals(generated, current.DeviceId))
                throw new InvalidOperationException("无法生成新的站点设备编号，原编号保持不变。");
            var next = checked(current.Version + 1);
            settings.Value.DeviceId = generated;
            Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1",
                ("$json", SerializeSettings(settings.Value)));
            Execute(connection, transaction, "INSERT INTO site_identity_operations VALUES($id,$version,1)", ("$id", operationId), ("$version", next));
            Execute(connection, transaction, "UPDATE site_identity SET version=$version,operation_id=$id,pending=1 WHERE id=1",
                ("$version", next), ("$id", operationId));
            var result = new SiteIdentitySnapshot(generated, next, operationId, true);
            cancellation.ThrowIfCancellationRequested();
            return new SiteIdentityOperationResult(result, operationId, next, true);
        });
    }

    /// <summary>仅当前未完成操作可转 ready；旧操作的完成重试不改变后续代次。</summary>
    public SiteIdentityOperationResult Complete(string operationId, CancellationToken cancellation = default)
    {
        operationId = CheckOperation(operationId);
        return WriteIdentity(cancellation, "站点配置正由另一项操作保存，更换尚未完成。请稍后继续原操作。", (connection, transaction) =>
        {
            var current = ReadCurrent(connection, transaction);
            var operation = ReadOperation(connection, transaction, operationId, current)
                ?? throw new InvalidOperationException("设备维护操作不存在，请重新加载页面。");
            if (!operation.Pending) return operation;
            if (current.OperationId != operationId || current.Version != operation.Version || !current.Pending)
                throw new InvalidOperationException("设备维护记录不一致，请恢复配套备份。");
            Execute(connection, transaction, "UPDATE site_identity_operations SET pending=0 WHERE operation_id=$id", ("$id", operationId));
            Execute(connection, transaction, "UPDATE site_identity SET pending=0 WHERE id=1");
            return operation with { Current = current with { Pending = false }, Pending = false };
        });
    }

    /// <summary>同仓储可取消排队，跨连接写锁立即尝试；生成与ready提交都不能在预算过期后继续。</summary>
    private T WriteIdentity<T>(CancellationToken cancellation, string busyMessage, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        // 同一仓储的重复提交先可取消地排队，进入SQLite后不再隐式等待另一写入者。
        writeGate.Wait(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = database.Open(timeoutSeconds: 1);
            using var transaction = connection.BeginTransaction(deferred: true);
            // 原生立即试写只获取写锁，不允许provider的busy重试越过维护截止时间。
            var resultCode = SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle!, 0);
            if (resultCode == SQLitePCL.raw.SQLITE_OK)
                resultCode = SQLitePCL.raw.sqlite3_exec(connection.Handle!, "UPDATE site_identity SET version=version WHERE id=1");
            if ((resultCode & 0xff) is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED) throw new InvalidOperationException(busyMessage);
            if (resultCode != SQLitePCL.raw.SQLITE_OK) throw new SqliteException("站点身份写入准备失败。", resultCode);
            cancellation.ThrowIfCancellationRequested();
            var result = action(connection, transaction);
            cancellation.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        // 成功、数据库忙、取消或故障都归还仓储互斥；未提交事务由释放自动回滚。
        finally { writeGate.Release(); }
    }

    /// <summary>读取并交叉校验当前记录和操作历史；唯一编号仅从设置取出。</summary>
    private static SiteIdentitySnapshot ReadCurrent(SqliteConnection connection, SqliteTransaction transaction)
    {
        var settings = ReadSettings(connection, transaction);
        using var command = Command(connection, transaction, """
            SELECT i.version,i.operation_id,i.pending,o.version,o.pending
            FROM site_identity i LEFT JOIN site_identity_operations o ON i.operation_id=o.operation_id WHERE i.id=1
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("站点身份状态缺失，请恢复配套备份。");
        var version = reader.GetInt64(0);
        var operation = reader.IsDBNull(1) ? null : reader.GetString(1);
        var pending = reader.GetInt32(2) == 1;
        if (version < 1 || (operation is null ? version != 1 || pending
            : reader.IsDBNull(3) || reader.GetInt64(3) != version || (reader.GetInt32(4) == 1) != pending))
            throw new InvalidOperationException("站点身份状态损坏，请恢复配套备份。");
        return new(settings.Value.DeviceId, version, operation, pending);
    }

    /// <summary>读取操作历史状态，不读取或保存历史设备编号。</summary>
    private static SiteIdentityOperationResult? ReadOperation(SqliteConnection connection, SqliteTransaction transaction, string operationId, SiteIdentitySnapshot current)
    {
        using var command = Command(connection, transaction, "SELECT version,pending FROM site_identity_operations WHERE operation_id=$id", ("$id", operationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(current, operationId, reader.GetInt64(0), reader.GetInt32(1) == 1) : null;
    }

    /// <summary>只接受 GUID 操作编号并统一书写形式，避免同一编号因大小写或短横线重复登记。</summary>
    private static string CheckOperation(string operationId) => Guid.TryParseExact(operationId, "N", out var id)
        || Guid.TryParseExact(operationId, "D", out id) ? id.ToString("N")
        : throw new InvalidOperationException("设备维护操作编号不正确，请重新加载页面。");
}
