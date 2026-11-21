using Core.Values;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

/// <summary>
/// 封闭取值进出库都按 <see cref="Wire{TEnum}"/> 的对照表换成今天存的那串字符（TEXT 列不变、库不升版本）；读到不认识的字符串时抛出并列出可选项，不兜底。
/// 不用 EF 自带的按枚举名转换：名字和库里的字符串不同（例如 not_applicable、api-key）。
/// </summary>
internal sealed class WireValueConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => Wire<TEnum>.Name(value),
    value => Wire<TEnum>.Parse(value)) where TEnum : struct, Enum;
