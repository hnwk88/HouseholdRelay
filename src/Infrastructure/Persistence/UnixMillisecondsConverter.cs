using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

/// <summary>
/// 全模型统一的时间换算：<see cref="DateTimeOffset"/> 存成 INTEGER 的 Unix 毫秒。SQLite 提供程序不能在库里比较和排序 DateTimeOffset，换成整数后可以；
/// 选毫秒是因为 upstream_candidates 的 created_at、deleted_at 在旧库里本来就是 Unix 毫秒，并进 BaseEntity 以后旧行的值一个字节不变。
/// </summary>
internal sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    value => DateTimeOffset.FromUnixTimeMilliseconds(value));
