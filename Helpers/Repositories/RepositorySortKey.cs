using System.Linq.Expressions;

namespace Repositories;

/// <summary>
/// 描述通用仓储分页查询的一个排序键。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
/// <param name="KeySelector">排序字段表达式。</param>
/// <param name="Descending">为 <see langword="true" /> 时降序排列。</param>
/// <remarks>
/// 本类型只承载「按哪个字段、朝哪个方向排」这一条通用分页信息，不表达任何业务含义，
/// 也不绑定具体工程名称、业务表名或数据库上下文类型。
/// 调用方按优先级从高到低传入若干个排序键，仓储依次翻译成 <c>ORDER BY</c> 的主键和后续次级键。
/// </remarks>
public sealed record RepositorySortKey<TEntity>(
    Expression<Func<TEntity, object>> KeySelector,
    bool Descending)
    where TEntity : BaseEntity;
