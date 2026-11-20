using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

/// <summary>
/// 金额照旧存成与区域无关的十进制文本（与 <c>UsageMoneyRules.DecimalText</c> 同一写法），读出时按同一区域解析，不经过浮点。
/// SQLite 提供程序不能在库里比较、求和 decimal；本工程从不在库里比较金额，合计在 Core 里按 decimal 累加。
/// </summary>
internal sealed class DecimalTextConverter() : ValueConverter<decimal, string>(
    value => value.ToString(CultureInfo.InvariantCulture),
    value => decimal.Parse(value, CultureInfo.InvariantCulture));
