using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Repositories;

/// <summary>
/// 提供基于 EF Core 的通用仓储实现。
/// </summary>
/// <typeparam name="TEntity">实体类型，必须继承自 <see cref="BaseEntity" />。</typeparam>
/// <remarks>
/// 本实现不写死具体工程名称、业务表名称或数据库上下文类型，构造函数只依赖 EF Core 的 <see cref="DbContext" />。
/// 提供单个、列表、条件、分页和导航属性预加载查询，以及添加、更新、软删除、物理删除和集中保存。
/// 默认查询遵守软删除过滤器；查询重载的 <c>includeDeleted</c> 只表示包含软删除记录。
/// 所有变更方法只修改当前工作单元，必须调用 <see cref="SaveChangesAsync" /> 才会真正提交数据库。
/// </remarks>
public class Repository<TEntity> : IRepository<TEntity>
    where TEntity : BaseEntity
{
    #region 字段

    /// <summary>
    /// 当前仓储使用的数据库上下文；使用 EF Core 基类避免把通用模块绑定到某个工程的上下文名称。
    /// </summary>
    private readonly DbContext _context;

    /// <summary>
    /// 当前实体类型对应的 EF Core 数据集合。
    /// </summary>
    private readonly DbSet<TEntity> _dbSet;

    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUserService _currentUserService;

    #endregion

    #region 构造函数

    /// <summary>
    /// 使用默认系统时钟和空用户上下文初始化仓储。
    /// </summary>
    /// <param name="context">数据库上下文；可以是任何继承自 <see cref="DbContext" /> 的工程上下文。</param>
    public Repository(DbContext context)
        : this(context, new DateTimeProvider(), new NullCurrentUserService())
    {
    }

    /// <summary>
    /// 使用可替换的时间和用户服务初始化仓储。
    /// </summary>
    /// <param name="context">数据库上下文；可以是任何继承自 <see cref="DbContext" /> 的工程上下文。</param>
    /// <param name="dateTimeProvider">时间提供者，用于稳定生成软删除和更新时间。</param>
    /// <param name="currentUserService">当前用户服务，只提供审计来源，不负责权限判断。</param>
    public Repository(
        DbContext context,
        IDateTimeProvider dateTimeProvider,
        ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dateTimeProvider);
        ArgumentNullException.ThrowIfNull(currentUserService);

        _context = context;
        _dbSet = context.Set<TEntity>();
        _dateTimeProvider = dateTimeProvider;
        _currentUserService = currentUserService;
    }

    #endregion

    #region 查询操作

    /// <summary>
    /// 根据主键 ID 获取未软删除实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>找到时返回实体，不存在或已经软删除时返回 <see langword="null" />。</returns>
    public Task<TEntity?> GetByIdAsync(
        long id,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return GetByIdAsync(id, includeDeleted: false, includes);
    }

    /// <summary>
    /// 根据主键 ID 获取实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>找到时返回实体，不存在时返回 <see langword="null" />。</returns>
    public Task<TEntity?> GetByIdAsync(
        long id,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return CreateQuery(includeDeleted, includes)
            .FirstOrDefaultAsync(entity => entity.Id == id);
    }

    /// <summary>
    /// 根据主键重新查询不进入当前身份映射的最新只读快照，同时保留全部默认安全过滤器。
    /// </summary>
    /// <param name="id">实体主键。</param>
    /// <returns>数据库当前快照；不存在、已软删除或超出宿主安全范围时为空。</returns>
    public Task<TEntity?> GetByIdSnapshotAsync(long id)
    {
        // AsNoTracking 强制重新物化数据库事实，避免同一 DbContext 已跟踪实例遮蔽并发工作单元刚提交的终态；
        // CreateQuery(false) 继续保留软删除、租户和诊所站点过滤器，快照读取不能扩大授权范围。
        return CreateQuery(includeDeleted: false, [])
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == id);
    }

    /// <summary>
    /// 获取全部未软删除实体。
    /// </summary>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>未软删除实体集合。</returns>
    public Task<List<TEntity>> GetAllAsync(
        params Expression<Func<TEntity, object>>[] includes)
    {
        return GetAllAsync(includeDeleted: false, includes);
    }

    /// <summary>
    /// 获取全部实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>符合删除范围要求的实体集合。</returns>
    public Task<List<TEntity>> GetAllAsync(
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return CreateQuery(includeDeleted, includes).ToListAsync();
    }

    /// <summary>
    /// 根据条件查询未软删除实体列表。
    /// </summary>
    /// <param name="predicate">查询条件；为空时返回全部未软删除实体。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>符合条件的实体集合。</returns>
    public Task<List<TEntity>> FindAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return FindAsync(predicate, includeDeleted: false, includes);
    }

    /// <summary>
    /// 根据条件查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件；为空时返回当前删除范围内全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>符合条件和删除范围要求的实体集合。</returns>
    public Task<List<TEntity>> FindAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes)
    {
        var query = CreateQuery(includeDeleted, includes);
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        return query.ToListAsync();
    }

    /// <summary>
    /// 根据条件查询第一个未软删除实体。
    /// </summary>
    /// <param name="predicate">查询条件表达式。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>第一个符合条件的实体，不存在时返回 <see langword="null" />。</returns>
    public Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return FirstOrDefaultAsync(predicate, includeDeleted: false, includes);
    }

    /// <summary>
    /// 根据条件查询第一个实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件表达式。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>第一个符合条件和删除范围要求的实体，不存在时返回 <see langword="null" />。</returns>
    public Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return CreateQuery(includeDeleted, includes).FirstOrDefaultAsync(predicate);
    }

    /// <summary>
    /// 判断是否存在符合条件的未软删除实体。
    /// </summary>
    /// <param name="predicate">查询条件；为空时检查是否存在任何未软删除实体。</param>
    /// <returns>存在返回 <see langword="true" />，否则返回 <see langword="false" />。</returns>
    public Task<bool> AnyAsync(Expression<Func<TEntity, bool>>? predicate = null)
    {
        return AnyAsync(predicate, includeDeleted: false);
    }

    /// <summary>
    /// 判断是否存在符合条件的实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件；为空时检查当前删除范围内是否存在任何实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>存在返回 <see langword="true" />，否则返回 <see langword="false" />。</returns>
    public Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted)
    {
        var query = CreateQuery(includeDeleted, []);
        return predicate is null ? query.AnyAsync() : query.AnyAsync(predicate);
    }

    /// <summary>
    /// 获取符合条件的未软删除实体数量。
    /// </summary>
    /// <param name="predicate">查询条件；为空时统计全部未软删除实体。</param>
    /// <returns>符合条件的实体数量。</returns>
    public Task<int> CountAsync(Expression<Func<TEntity, bool>>? predicate = null)
    {
        return CountAsync(predicate, includeDeleted: false);
    }

    /// <summary>
    /// 获取符合条件的实体数量，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件；为空时统计当前删除范围内全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>符合条件和删除范围要求的实体数量。</returns>
    public Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted)
    {
        var query = CreateQuery(includeDeleted, []);
        return predicate is null ? query.CountAsync() : query.CountAsync(predicate);
    }

    /// <summary>
    /// 分页查询未软删除实体列表。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量。</param>
    /// <param name="predicate">查询条件；为空时查询全部未软删除实体。</param>
    /// <param name="orderBy">排序字段；为空时不附加排序。</param>
    /// <param name="descending">是否降序排列。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>当前页实体集合和分页前总记录数。</returns>
    public Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate = null,
        Expression<Func<TEntity, object>>? orderBy = null,
        bool descending = false,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return GetPagedAsync(
            pageIndex,
            pageSize,
            includeDeleted: false,
            predicate,
            orderBy,
            descending,
            includes);
    }

    /// <summary>
    /// 分页查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="predicate">查询条件；为空时查询当前删除范围内全部实体。</param>
    /// <param name="orderBy">排序字段；为空时不附加排序。</param>
    /// <param name="descending">是否降序排列。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>当前页实体集合和分页前总记录数。</returns>
    public async Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        bool includeDeleted,
        Expression<Func<TEntity, bool>>? predicate = null,
        Expression<Func<TEntity, object>>? orderBy = null,
        bool descending = false,
        params Expression<Func<TEntity, object>>[] includes)
    {
        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "页码必须从 1 开始。");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "每页数量必须大于 0。");
        }

        var query = CreateQuery(includeDeleted, includes);
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        var totalCount = await query.CountAsync();
        if (orderBy is not null)
        {
            query = descending
                ? query.OrderByDescending(orderBy)
                : query.OrderBy(orderBy);
        }

        var items = await query
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
    }

    /// <summary>
    /// 按多个排序键稳定分页查询未软删除实体列表。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量。</param>
    /// <param name="predicate">查询条件；为空时查询全部未软删除实体。</param>
    /// <param name="sortKeys">按优先级从高到低排列的排序键。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>当前页实体集合和分页前总记录数。</returns>
    public Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate,
        IReadOnlyList<RepositorySortKey<TEntity>> sortKeys,
        params Expression<Func<TEntity, object>>[] includes)
    {
        return GetPagedAsync(
            pageIndex,
            pageSize,
            includeDeleted: false,
            predicate,
            sortKeys,
            includes);
    }

    /// <summary>
    /// 按多个排序键稳定分页查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="predicate">查询条件；为空时查询当前删除范围内全部实体。</param>
    /// <param name="sortKeys">按优先级从高到低排列的排序键。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>当前页实体集合和分页前总记录数。</returns>
    public async Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        bool includeDeleted,
        Expression<Func<TEntity, bool>>? predicate,
        IReadOnlyList<RepositorySortKey<TEntity>> sortKeys,
        params Expression<Func<TEntity, object>>[] includes)
    {
        ArgumentNullException.ThrowIfNull(sortKeys);
        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "页码必须从 1 开始。");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "每页数量必须大于 0。");
        }

        var query = CreateQuery(includeDeleted, includes);
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        var totalCount = await query.CountAsync();
        IOrderedQueryable<TEntity>? orderedQuery = null;
        foreach (RepositorySortKey<TEntity> sortKey in sortKeys)
        {
            // 第一个键用 OrderBy 建立顺序，后续键必须用 ThenBy 叠加；
            // 再调一次 OrderBy 会把前面的键整个丢掉，分页顺序就不再稳定。
            orderedQuery = orderedQuery is null
                ? sortKey.Descending
                    ? query.OrderByDescending(sortKey.KeySelector)
                    : query.OrderBy(sortKey.KeySelector)
                : sortKey.Descending
                    ? orderedQuery.ThenByDescending(sortKey.KeySelector)
                    : orderedQuery.ThenBy(sortKey.KeySelector);
        }

        var items = await (orderedQuery ?? query)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
    }

    /// <summary>
    /// 按指定键在数据库端分组统计未软删除实体数量。
    /// </summary>
    /// <typeparam name="TKey">分组键类型。</typeparam>
    /// <param name="keySelector">分组键表达式。</param>
    /// <param name="predicate">分组前的筛选条件；为空时统计全部未软删除实体。</param>
    /// <returns>每个键及其记录数。</returns>
    public Task<List<(TKey Key, int Count)>> GroupCountAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? predicate = null)
    {
        return GroupCountAsync(keySelector, predicate, includeDeleted: false);
    }

    /// <summary>
    /// 按指定键在数据库端分组统计实体数量，并显式选择是否包含软删除数据。
    /// </summary>
    /// <typeparam name="TKey">分组键类型。</typeparam>
    /// <param name="keySelector">分组键表达式。</param>
    /// <param name="predicate">分组前的筛选条件；为空时统计当前删除范围内全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>每个键及其记录数。</returns>
    /// <remarks>
    /// 分组和计数都由查询提供程序翻译成一条聚合语句执行，返回的行数等于分组数而不是实体数，
    /// 因此不需要、也不会把候选实体物化进内存。
    /// </remarks>
    public async Task<List<(TKey Key, int Count)>> GroupCountAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var query = CreateQuery(includeDeleted, []);
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        // 投影成匿名类型而不是元组：元组的字段名在表达式树里没有稳定形态，
        // 部分查询提供程序会因此拒绝翻译投影。
        var rows = await query
            .GroupBy(keySelector)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync();
        return rows.Select(row => (row.Key, row.Count)).ToList();
    }

    #endregion

    #region 添加操作

    /// <summary>
    /// 添加单个实体到当前工作单元。
    /// </summary>
    /// <param name="entity">要添加的实体对象。</param>
    /// <returns>已经加入跟踪的实体。</returns>
    public async Task<TEntity> AddAsync(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _dbSet.AddAsync(entity);
        return entity;
    }

    /// <summary>
    /// 批量添加实体到当前工作单元。
    /// </summary>
    /// <param name="entities">要添加的实体集合。</param>
    /// <returns>已经加入跟踪的实体集合。</returns>
    public async Task<List<TEntity>> AddRangeAsync(List<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        await _dbSet.AddRangeAsync(entities);
        return entities;
    }

    #endregion

    #region 更新操作

    /// <summary>
    /// 更新单个实体；已跟踪实体保留真实变更检测，脱离跟踪实体才按整实体更新处理。
    /// </summary>
    /// <param name="entity">要更新的实体对象。</param>
    /// <returns>已经纳入当前更新工作单元的实体。</returns>
    public Task<TEntity> UpdateAsync(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        TrackForUpdate(entity);
        return Task.FromResult(entity);
    }

    /// <summary>
    /// 批量更新实体；逐项复用单实体跟踪策略，避免已跟踪实体被误标为整行修改。
    /// </summary>
    /// <param name="entities">要更新的实体集合。</param>
    /// <returns>已经纳入当前更新工作单元的实体集合。</returns>
    public Task<List<TEntity>> UpdateRangeAsync(List<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        foreach (var entity in entities)
        {
            TrackForUpdate(entity);
        }

        return Task.FromResult(entities);
    }

    #endregion

    #region 物理删除操作

    /// <summary>
    /// 根据主键 ID 物理删除实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <returns>实体存在并登记物理删除返回 <see langword="true" />。</returns>
    public async Task<bool> DeleteAsync(long id)
    {
        var entity = await GetByIdAsync(id, includeDeleted: true);
        return entity is not null && await DeleteAsync(entity);
    }

    /// <summary>
    /// 根据主键 ID 删除实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>实体存在并进入目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> DeleteAsync(long id, bool isHardDelete)
    {
        return isHardDelete ? DeleteAsync(id) : SoftDeleteAsync(id);
    }

    /// <summary>
    /// 物理删除指定实体。
    /// </summary>
    /// <param name="entity">要物理删除的实体对象。</param>
    /// <returns>成功登记物理删除返回 <see langword="true" />。</returns>
    public Task<bool> DeleteAsync(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // 当前工程的 DbContext 会把普通 Deleted 状态转换为软删除；只有仓储显式登记后，
        // 上下文才能确认这是应用层已经授权的物理删除，避免硬删除开关被静默改回软删除。
        if (_context is IHardDeleteTracker hardDeleteTracker)
        {
            hardDeleteTracker.MarkHardDelete(entity);
        }

        _dbSet.Remove(entity);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 删除指定实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="entity">要删除的实体对象。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>成功进入目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> DeleteAsync(TEntity entity, bool isHardDelete)
    {
        return isHardDelete ? DeleteAsync(entity) : SoftDeleteAsync(entity);
    }

    /// <summary>
    /// 批量物理删除实体。
    /// </summary>
    /// <param name="entities">要物理删除的实体集合。</param>
    /// <returns>成功登记整批物理删除返回 <see langword="true" />。</returns>
    public Task<bool> DeleteRangeAsync(List<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (_context is IHardDeleteTracker hardDeleteTracker)
        {
            foreach (var entity in entities)
            {
                hardDeleteTracker.MarkHardDelete(entity);
            }
        }

        _dbSet.RemoveRange(entities);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 批量删除实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="entities">要删除的实体集合。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>成功登记整批目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> DeleteRangeAsync(List<TEntity> entities, bool isHardDelete)
    {
        return isHardDelete ? DeleteRangeAsync(entities) : SoftDeleteRangeAsync(entities);
    }

    #endregion

    #region 软删除操作

    /// <summary>
    /// 根据主键 ID 软删除实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <returns>实体存在并完成软删除标记返回 <see langword="true" />。</returns>
    public async Task<bool> SoftDeleteAsync(long id)
    {
        var entity = await GetByIdAsync(id);
        return entity is not null && await SoftDeleteAsync(entity);
    }

    /// <summary>
    /// 根据主键 ID 执行删除，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>实体存在并进入目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> SoftDeleteAsync(long id, bool isHardDelete)
    {
        return isHardDelete ? DeleteAsync(id) : SoftDeleteAsync(id);
    }

    /// <summary>
    /// 软删除指定实体，并只标记软删除和审计字段。
    /// </summary>
    /// <param name="entity">要软删除的实体对象。</param>
    /// <returns>成功完成软删除标记返回 <see langword="true" />。</returns>
    public Task<bool> SoftDeleteAsync(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ApplySoftDelete(entity);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 删除指定实体，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="entity">要删除的实体对象。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>成功进入目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> SoftDeleteAsync(TEntity entity, bool isHardDelete)
    {
        return isHardDelete ? DeleteAsync(entity) : SoftDeleteAsync(entity);
    }

    /// <summary>
    /// 批量软删除实体，并在内存中统一设置删除审计字段。
    /// </summary>
    /// <param name="entities">要软删除的实体集合。</param>
    /// <returns>成功设置整批软删除字段返回 <see langword="true" />。</returns>
    public Task<bool> SoftDeleteRangeAsync(List<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        foreach (var entity in entities)
        {
            ApplySoftDelete(entity);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// 批量删除实体，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="entities">要删除的实体集合。</param>
    /// <param name="isHardDelete">是否执行物理删除。</param>
    /// <returns>成功登记整批目标删除状态返回 <see langword="true" />。</returns>
    public Task<bool> SoftDeleteRangeAsync(List<TEntity> entities, bool isHardDelete)
    {
        return isHardDelete ? DeleteRangeAsync(entities) : SoftDeleteRangeAsync(entities);
    }

    #endregion

    #region 持久化操作

    /// <summary>
    /// 保存当前工作单元中的全部更改到数据库。
    /// </summary>
    /// <returns>受影响的记录数。</returns>
    public Task<int> SaveChangesAsync()
    {
        return _context.SaveChangesAsync();
    }

    /// <summary>
    /// 在当前数据库上下文的一个事务里执行操作，正常返回才提交；上下文里已有事务时直接在其中执行。
    /// </summary>
    /// <typeparam name="TResult">操作的返回值类型。</typeparam>
    /// <param name="operation">要在事务里执行的操作。</param>
    /// <returns>操作的结果。</returns>
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // 同一上下文里的多个仓储共用一个事务；外层已经开了事务时不再嵌套，由外层决定提交或回滚。
        if (_context.Database.CurrentTransaction is not null)
        {
            return await operation();
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();
        var result = await operation();
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>
    /// 在一段操作期间把当前上下文的命令等待上限设成 <paramref name="lockWait"/>（不足一秒按一秒），结束后还原原值。
    /// </summary>
    /// <typeparam name="TResult">操作的返回值类型。</typeparam>
    /// <param name="lockWait">每条命令最多等多久。</param>
    /// <param name="operation">要执行的操作。</param>
    /// <returns>操作的结果。</returns>
    public async Task<TResult> WithLockWaitAsync<TResult>(TimeSpan lockWait, Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockWait, TimeSpan.Zero);
        var previous = _context.Database.GetCommandTimeout();
        // 关系型提供程序的命令超时只认整秒，0 表示无限等待；不足一秒的上限按一秒算，绝不变成无限。
        _context.Database.SetCommandTimeout(Math.Max(1, (int)Math.Ceiling(lockWait.TotalSeconds)));
        try { return await operation(); }
        // 同一上下文之后的操作要回到原来的等待上限，成功或抛出都还原。
        finally { _context.Database.SetCommandTimeout(previous); }
    }

    #endregion

    /// <summary>
    /// 创建统一查询入口，并按需忽略软删除过滤器和加载导航属性。
    /// </summary>
    /// <param name="includeDeleted">是否包含软删除数据。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>已经应用删除范围和导航加载的查询对象。</returns>
    private IQueryable<TEntity> CreateQuery(
        bool includeDeleted,
        IReadOnlyCollection<Expression<Func<TEntity, object>>> includes)
    {
        IQueryable<TEntity> query = _dbSet;
        if (includeDeleted)
        {
            // EF Core 10 的命名过滤器允许只关闭软删除过滤器；租户过滤器必须继续生效，
            // 避免维护人员为了查看已删除记录而越权读取其它诊所的数据。
            query = query.IgnoreQueryFilters([RepositoryFilterNames.SoftDeletion]);
        }

        foreach (var include in includes)
        {
            query = query.Include(include);
        }

        return query;
    }

    /// <summary>
    /// 设置单个实体的软删除和审计字段，并确保只修改这些受控字段。
    /// </summary>
    /// <param name="entity">要软删除的实体。</param>
    private void ApplySoftDelete(TEntity entity)
    {
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            _dbSet.Attach(entity);
        }

        var now = _dateTimeProvider.UtcNowOffset;
        entity.IsDeleted = true;
        entity.DeletedAt = now;
        entity.DeletedBy = ResolveCurrentActor();
        entity.UpdatedAt = now;
        entity.UpdatedBy = entity.DeletedBy;

        // 软删除只允许修改统一审计字段，不能因为实体来自列表快照而把其它旧字段整体覆盖回数据库。
        entry.Property(item => item.IsDeleted).IsModified = true;
        entry.Property(item => item.DeletedAt).IsModified = true;
        entry.Property(item => item.DeletedBy).IsModified = true;
        entry.Property(item => item.UpdatedAt).IsModified = true;
        entry.Property(item => item.UpdatedBy).IsModified = true;
    }

    /// <summary>
    /// 把实体纳入更新工作单元，同时避免把已跟踪实体的所有字段误标为已修改。
    /// </summary>
    /// <param name="entity">待保存的实体。</param>
    private void TrackForUpdate(TEntity entity)
    {
        var entry = _context.Entry(entity);
        if (entry.State != EntityState.Detached)
        {
            // 已由当前 DbContext 查询出的实体会在 SaveChanges 前通过变更检测识别真实字段；
            // 不能再次调用 Update，否则 EF 会把未变化的 long 外键和来源事实整行标记为 Modified，
            // 导致旧业务承接白名单误判为篡改历史关系。
            return;
        }

        // Detached 实体没有原始快照，只能沿用既有整实体更新语义；调用方仍需提供行版本和业务门禁。
        _dbSet.Update(entity);
    }

    /// <summary>
    /// 解析当前审计来源，优先使用稳定用户标识，没有标识时再使用显示名称。
    /// </summary>
    /// <returns>当前用户或调用来源；不可用时返回空。</returns>
    private string? ResolveCurrentActor()
    {
        return !string.IsNullOrWhiteSpace(_currentUserService.UserId)
            ? _currentUserService.UserId
            : _currentUserService.UserName;
    }
}
