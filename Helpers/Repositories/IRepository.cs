using System.Linq.Expressions;

namespace Repositories;

/// <summary>
/// 泛型仓储接口，提供不绑定具体工程名称和具体数据库上下文的基础数据访问操作。
/// </summary>
/// <typeparam name="TEntity">实体类型，必须继承自 <see cref="BaseEntity" /> 基类。</typeparam>
/// <remarks>
/// 默认查询遵守工程配置的软删除过滤器；带 <c>includeDeleted</c> 的查询重载可以读取软删除数据。
/// 物理删除后的记录已经从数据库中消失，任何仓储都无法再次查询，因此“包含删除数据”只表示包含软删除记录。
/// 添加、更新、软删除和物理删除只改变当前工作单元，调用方仍需调用 <see cref="SaveChangesAsync" /> 集中提交。
/// </remarks>
public interface IRepository<TEntity>
    where TEntity : BaseEntity
{
    #region 查询操作

    /// <summary>
    /// 根据主键 ID 获取单个实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>找到时返回实体，不存在或已经软删除时返回 <see langword="null" />。</returns>
    /// <remarks>
    /// 默认遵守软删除查询过滤器；需要读取软删除实体时使用带 <c>includeDeleted</c> 的重载。
    /// </remarks>
    /// <example>
    /// <code>await repository.GetByIdAsync(id, item => item.Department, item => item.Orders);</code>
    /// </example>
    Task<TEntity?> GetByIdAsync(
        long id,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据主键 ID 获取单个实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器；为 <see langword="true" /> 时可以返回软删除实体。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>找到时返回实体，不存在时返回 <see langword="null" />。</returns>
    /// <remarks>
    /// 该开关不能查询已经物理删除的数据，因为物理删除记录已经永久离开数据库。
    /// </remarks>
    Task<TEntity?> GetByIdAsync(
        long id,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据主键 ID 重新查询一个不进入当前 DbContext 身份映射的最新只读快照。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <returns>返回当前软删除和宿主安全过滤器范围内的最新实体快照，不存在时返回 <see langword="null" />。</returns>
    /// <remarks>
    /// 本方法解决“首次读取后，另一个工作单元已经提交终态，但当前跟踪实体不能被普通查询覆盖”的通用搬运缺口。
    /// 应用层内存计算无法重新取得数据库提交事实，强制刷新受跟踪实体又会覆盖当前工作单元修改，因此仓储使用
    /// <c>AsNoTracking</c> 发起独立物化。该方法仍遵守软删除、租户和诊所站点过滤器，不提供 IncludeDeleted，
    /// 也不允许调用方保存返回快照；需要修改实体时继续使用 <see cref="GetByIdAsync(long, Expression{Func{TEntity, object}}[])" />。
    /// </remarks>
    Task<TEntity?> GetByIdSnapshotAsync(long id);

    /// <summary>
    /// 获取所有未软删除实体。
    /// </summary>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回未软删除实体集合。</returns>
    /// <remarks>
    /// 数据量较大时应优先使用条件查询或分页查询，避免一次把整表对象图加载到内存。
    /// </remarks>
    Task<List<TEntity>> GetAllAsync(
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 获取所有实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回符合删除范围要求的实体集合。</returns>
    Task<List<TEntity>> GetAllAsync(
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据条件查询未软删除实体列表。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时返回全部未软删除实体。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回符合条件的实体集合。</returns>
    /// <example>
    /// <code>await repository.FindAsync(item => item.Name == "示例", item => item.Department);</code>
    /// </example>
    Task<List<TEntity>> FindAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据条件查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时返回当前删除范围内的全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回符合条件和删除范围要求的实体集合。</returns>
    Task<List<TEntity>> FindAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据条件查询第一个未软删除实体。
    /// </summary>
    /// <param name="predicate">查询条件表达式。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回第一个符合条件的实体，不存在时返回 <see langword="null" />。</returns>
    Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 根据条件查询第一个实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件表达式。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回第一个符合条件和删除范围要求的实体，不存在时返回 <see langword="null" />。</returns>
    Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDeleted,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 判断是否存在符合条件的未软删除实体。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时检查是否存在任何未软删除实体。</param>
    /// <returns>存在返回 <see langword="true" />，否则返回 <see langword="false" />。</returns>
    Task<bool> AnyAsync(Expression<Func<TEntity, bool>>? predicate = null);

    /// <summary>
    /// 判断是否存在符合条件的实体，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时检查当前删除范围内是否存在任何实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>存在返回 <see langword="true" />，否则返回 <see langword="false" />。</returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted);

    /// <summary>
    /// 获取符合条件的未软删除实体数量。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时统计全部未软删除实体。</param>
    /// <returns>返回符合条件的实体数量。</returns>
    Task<int> CountAsync(Expression<Func<TEntity, bool>>? predicate = null);

    /// <summary>
    /// 获取符合条件的实体数量，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="predicate">查询条件表达式；为空时统计当前删除范围内全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>返回符合条件和删除范围要求的实体数量。</returns>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted);

    /// <summary>
    /// 按指定键在数据库端分组统计未软删除实体数量。
    /// </summary>
    /// <typeparam name="TKey">分组键类型；必须是查询提供程序能翻译的表达式结果。</typeparam>
    /// <param name="keySelector">分组键表达式。</param>
    /// <param name="predicate">分组前的筛选条件；为空时统计全部未软删除实体。</param>
    /// <returns>返回每个键及其记录数，顺序由查询提供程序决定，调用方需要自行排序。</returns>
    /// <remarks>
    /// <b>[REPOSITORY-PARAM-001] 新增能力登记。</b>
    /// <list type="bullet">
    /// <item><b>通用搬运缺口</b>：现有合同只能整表取回实体（<see cref="FindAsync(Expression{Func{TEntity, bool}}, Expression{Func{TEntity, object}}[])" />）
    /// 或统计单一总数（<see cref="CountAsync(Expression{Func{TEntity, bool}})" />），没有「一次取回按键分组的计数」这一 SQL 基本聚合能力。</item>
    /// <item><b>应用层为何无法解决</b>：应用层要得到分组计数只能先把候选行整表物化再在内存 <c>GroupBy</c>；
    /// 订单与收费各有八万行，导航树每展开一级就要重来一次，物化成本本身就是问题，内存计算无法消除。</item>
    /// <item><b>复用场景</b>：订单、收费、处方、就诊、会员卡和科室六个日期导航树的年、月、日逐级计数都用它；
    /// 分组计数也是 <c>GROUP BY … COUNT(*)</c> 这一数据库通用机制，不绑定任何业务语义。</item>
    /// <item><b>受影响调用方与兼容策略</b>：纯新增，既有方法签名和语义一律不变；未实现该方法的测试替身只在用到分组计数时才需要配置。</item>
    /// <item><b>数据与事务风险</b>：只读聚合，不进入变更跟踪，不登记任何待写实体，因此不改变工作单元状态，也没有失败清理动作；
    /// 与其它查询一样遵守软删除、租户和诊所站点过滤器。</item>
    /// <item><b>验证命令</b>：<c>dotnet test ZKMedicalSystem.sln</c>；
    /// <c>py -3 -B tools/doc_maintainer.py code-check --level full --scope all</c>。</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>await repository.GroupCountAsync(item => item.BusinessDate!.Value.Year, item => item.BusinessDate != null);</code>
    /// </example>
    Task<List<(TKey Key, int Count)>> GroupCountAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? predicate = null);

    /// <summary>
    /// 按指定键在数据库端分组统计实体数量，并显式选择是否包含软删除数据。
    /// </summary>
    /// <typeparam name="TKey">分组键类型。</typeparam>
    /// <param name="keySelector">分组键表达式。</param>
    /// <param name="predicate">分组前的筛选条件；为空时统计当前删除范围内全部实体。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <returns>返回每个键及其记录数。</returns>
    Task<List<(TKey Key, int Count)>> GroupCountAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? predicate,
        bool includeDeleted);

    /// <summary>
    /// 分页查询未软删除实体列表。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量，必须大于 0。</param>
    /// <param name="predicate">查询条件表达式；为空时查询全部未软删除实体。</param>
    /// <param name="orderBy">排序字段表达式；为空时不附加排序。</param>
    /// <param name="descending">是否降序排列，默认按升序排列。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回当前页实体集合和分页前的总记录数。</returns>
    Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate = null,
        Expression<Func<TEntity, object>>? orderBy = null,
        bool descending = false,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 分页查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量，必须大于 0。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="predicate">查询条件表达式；为空时查询当前删除范围内全部实体。</param>
    /// <param name="orderBy">排序字段表达式；为空时不附加排序。</param>
    /// <param name="descending">是否降序排列。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回当前页实体集合和分页前的总记录数。</returns>
    Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        bool includeDeleted,
        Expression<Func<TEntity, bool>>? predicate = null,
        Expression<Func<TEntity, object>>? orderBy = null,
        bool descending = false,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 按多个排序键稳定分页查询未软删除实体列表。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量，必须大于 0。</param>
    /// <param name="predicate">查询条件表达式；为空时查询全部未软删除实体。</param>
    /// <param name="sortKeys">按优先级从高到低排列的排序键；为空集合时不附加排序。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回当前页实体集合和分页前的总记录数。</returns>
    /// <remarks>
    /// <b>[REPOSITORY-PARAM-001] 新增能力登记。</b>
    /// <list type="bullet">
    /// <item><b>通用搬运缺口</b>：既有分页重载只接受一个排序字段，主键或时间相同的行在数据库端没有确定顺序，
    /// 翻页时同一行可能重复出现或整页漏掉。稳定分页需要「多个排序键」这一分页固有能力。</item>
    /// <item><b>应用层为何无法解决</b>：稳定顺序必须与 <c>OFFSET/FETCH</c> 在同一条语句里成立；
    /// 应用层拿到的已经是被截断的一页，无法事后补齐次级排序。唯一的内存替代是先整表取回再排序分页，那正是本次要消除的做法。</item>
    /// <item><b>复用场景</b>：订单列表按业务日期、创建时间、主键三级排序；收费列表按有效收费日期和主键两级排序；
    /// 多键排序也是 <c>ORDER BY a, b, c</c> 这一数据库通用机制。</item>
    /// <item><b>受影响调用方与兼容策略</b>：纯新增重载，单排序键的既有调用方全部保持原语义。</item>
    /// <item><b>数据与事务风险</b>：只读查询，不进入变更跟踪，没有失败清理动作；仍遵守软删除、租户和诊所站点过滤器。</item>
    /// <item><b>验证命令</b>：<c>dotnet test ZKMedicalSystem.sln</c>；
    /// <c>py -3 -B tools/doc_maintainer.py code-check --level full --scope all</c>。</item>
    /// </list>
    /// </remarks>
    Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate,
        IReadOnlyList<RepositorySortKey<TEntity>> sortKeys,
        params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// 按多个排序键稳定分页查询实体列表，并显式选择是否包含软删除数据。
    /// </summary>
    /// <param name="pageIndex">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数据量，必须大于 0。</param>
    /// <param name="includeDeleted">是否忽略软删除查询过滤器。</param>
    /// <param name="predicate">查询条件表达式；为空时查询当前删除范围内全部实体。</param>
    /// <param name="sortKeys">按优先级从高到低排列的排序键；为空集合时不附加排序。</param>
    /// <param name="includes">要预加载的导航属性表达式数组。</param>
    /// <returns>返回当前页实体集合和分页前的总记录数。</returns>
    Task<(List<TEntity> Items, int TotalCount)> GetPagedAsync(
        int pageIndex,
        int pageSize,
        bool includeDeleted,
        Expression<Func<TEntity, bool>>? predicate,
        IReadOnlyList<RepositorySortKey<TEntity>> sortKeys,
        params Expression<Func<TEntity, object>>[] includes);

    #endregion

    #region 添加操作

    /// <summary>
    /// 添加单个实体到当前工作单元。
    /// </summary>
    /// <param name="entity">要添加的实体对象。</param>
    /// <returns>返回已经加入跟踪的实体。</returns>
    /// <remarks>
    /// 本方法不提交数据库；调用方应在完成业务计算后调用 <see cref="SaveChangesAsync" /> 集中保存。
    /// </remarks>
    Task<TEntity> AddAsync(TEntity entity);

    /// <summary>
    /// 批量添加实体到当前工作单元。
    /// </summary>
    /// <param name="entities">要添加的实体集合。</param>
    /// <returns>返回已经加入跟踪的实体集合。</returns>
    /// <remarks>
    /// 批量添加只调用一次 EF Core 批量跟踪入口，不在循环中逐条提交数据库。
    /// </remarks>
    Task<List<TEntity>> AddRangeAsync(List<TEntity> entities);

    #endregion

    #region 更新操作

    /// <summary>
    /// 更新单个实体；已跟踪实体保留真实变更检测，脱离跟踪实体才按整实体更新处理。
    /// </summary>
    /// <param name="entity">要更新的实体对象。</param>
    /// <returns>返回已经纳入当前更新工作单元的实体。</returns>
    /// <remarks>
    /// 已由当前 DbContext 查询出的实体会保留 EF 原始快照，只提交真实变更字段；调用方传入脱离跟踪实体时，
    /// 通用仓储无法获知原始快照，仍按整实体更新语义处理。
    /// </remarks>
    Task<TEntity> UpdateAsync(TEntity entity);

    /// <summary>
    /// 批量更新实体。
    /// </summary>
    /// <param name="entities">要更新的实体集合。</param>
    /// <returns>返回已经纳入当前更新工作单元的实体集合。</returns>
    /// <remarks>
    /// 本方法不逐条保存，调用方应在批量计算结束后统一调用 <see cref="SaveChangesAsync" />。
    /// </remarks>
    Task<List<TEntity>> UpdateRangeAsync(List<TEntity> entities);

    #endregion

    #region 删除操作

    /// <summary>
    /// 根据主键 ID 物理删除实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <returns>实体存在并进入物理删除状态时返回 <see langword="true" />，不存在时返回 <see langword="false" />。</returns>
    /// <remarks>
    /// 物理删除无法恢复。应用服务必须先完成权限、引用关系、清理范围和删除原因判断，再调用本方法。
    /// 本方法不立即提交数据库，仍需调用 <see cref="SaveChangesAsync" />。
    /// </remarks>
    Task<bool> DeleteAsync(long id);

    /// <summary>
    /// 根据主键 ID 删除实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时物理删除；为 <see langword="false" /> 时软删除。</param>
    /// <returns>实体存在并成功进入目标删除状态时返回 <see langword="true" />。</returns>
    Task<bool> DeleteAsync(long id, bool isHardDelete);

    /// <summary>
    /// 物理删除指定实体。
    /// </summary>
    /// <param name="entity">要物理删除的实体对象。</param>
    /// <returns>成功登记物理删除返回 <see langword="true" />。</returns>
    Task<bool> DeleteAsync(TEntity entity);

    /// <summary>
    /// 删除指定实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="entity">要删除的实体对象。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时物理删除；为 <see langword="false" /> 时软删除。</param>
    /// <returns>成功进入目标删除状态返回 <see langword="true" />。</returns>
    Task<bool> DeleteAsync(TEntity entity, bool isHardDelete);

    /// <summary>
    /// 批量物理删除实体。
    /// </summary>
    /// <param name="entities">要物理删除的实体集合。</param>
    /// <returns>成功登记整批物理删除返回 <see langword="true" />。</returns>
    /// <remarks>
    /// 本方法使用批量状态登记，不在循环中调用 <see cref="SaveChangesAsync" />。
    /// </remarks>
    Task<bool> DeleteRangeAsync(List<TEntity> entities);

    /// <summary>
    /// 批量删除实体，并通过开关选择软删除或物理删除。
    /// </summary>
    /// <param name="entities">要删除的实体集合。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时物理删除；为 <see langword="false" /> 时软删除。</param>
    /// <returns>成功登记整批目标删除状态返回 <see langword="true" />。</returns>
    Task<bool> DeleteRangeAsync(List<TEntity> entities, bool isHardDelete);

    #endregion

    #region 软删除操作

    /// <summary>
    /// 根据主键 ID 软删除实体。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <returns>实体存在并完成软删除标记时返回 <see langword="true" />。</returns>
    /// <remarks>
    /// 软删除设置 <see cref="BaseEntity.IsDeleted" />、<see cref="BaseEntity.DeletedAt" /> 和审计来源，不会立即从数据库移除记录。
    /// </remarks>
    Task<bool> SoftDeleteAsync(long id);

    /// <summary>
    /// 根据主键 ID 执行删除，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="id">实体主键 ID。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时改为物理删除；否则保持软删除。</param>
    /// <returns>实体存在并进入目标删除状态时返回 <see langword="true" />。</returns>
    Task<bool> SoftDeleteAsync(long id, bool isHardDelete);

    /// <summary>
    /// 软删除指定实体。
    /// </summary>
    /// <param name="entity">要软删除的实体对象。</param>
    /// <returns>成功设置软删除和审计字段返回 <see langword="true" />。</returns>
    Task<bool> SoftDeleteAsync(TEntity entity);

    /// <summary>
    /// 删除指定实体，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="entity">要删除的实体对象。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时改为物理删除；否则保持软删除。</param>
    /// <returns>成功进入目标删除状态返回 <see langword="true" />。</returns>
    Task<bool> SoftDeleteAsync(TEntity entity, bool isHardDelete);

    /// <summary>
    /// 批量软删除实体。
    /// </summary>
    /// <param name="entities">要软删除的实体集合。</param>
    /// <returns>成功设置整批软删除和审计字段返回 <see langword="true" />。</returns>
    Task<bool> SoftDeleteRangeAsync(List<TEntity> entities);

    /// <summary>
    /// 批量删除实体，并兼容通过开关升级为物理删除。
    /// </summary>
    /// <param name="entities">要删除的实体集合。</param>
    /// <param name="isHardDelete">为 <see langword="true" /> 时改为物理删除；否则保持软删除。</param>
    /// <returns>成功登记整批目标删除状态返回 <see langword="true" />。</returns>
    Task<bool> SoftDeleteRangeAsync(List<TEntity> entities, bool isHardDelete);

    #endregion

    #region 持久化操作

    /// <summary>
    /// 保存当前工作单元中的全部更改到数据库。
    /// </summary>
    /// <returns>返回受影响的记录数。</returns>
    /// <remarks>
    /// 添加、更新、软删除和物理删除应先在内存中集中完成，再调用本方法一次提交。
    /// 如果调用方需要跨多个仓储的原子事务，应由工程级事务适配器包裹同一数据库上下文。
    /// </remarks>
    Task<int> SaveChangesAsync();

    /// <summary>
    /// 在当前数据库上下文的一个事务里执行一段「先读、判断、再写」的操作，操作正常返回才提交，抛出异常则整体回滚。
    /// </summary>
    /// <typeparam name="TResult">操作的返回值类型。</typeparam>
    /// <param name="operation">要在事务里执行的操作；其中的查询与 <see cref="SaveChangesAsync" /> 都落在同一个事务里。</param>
    /// <returns>返回操作的结果。</returns>
    /// <remarks>
    /// <b>[REPOSITORY-PARAM-002] 新增能力登记（HouseholdRelay 本工程扩展，中康原版没有）。</b>
    /// <list type="bullet">
    /// <item><b>通用搬运缺口</b>：现有合同里查询和 <see cref="SaveChangesAsync" /> 各自独立执行，读出来的事实与随后的写入之间没有原子边界；
    /// 「读出名单再判断人数上限」「读出版本号再写新版本」这类先读后写，在两个进程同时写时会各自读到旧事实、双双写成。</item>
    /// <item><b>应用层为何无法解决</b>：应用层只拿得到仓储，拿不到数据库上下文和事务；并发令牌只能保护被改写的那一行，
    /// 保护不了「插入一行之前数过的行数」或「另一张表的某一行此刻仍未变」这类跨行事实。原子边界只能由持有上下文的一方给出。</item>
    /// <item><b>复用场景</b>：使用者人数上限、管理员首次设置、上游激活与停用、按版本保存设置、站点身份换号；
    /// 「事务里先读后写」也是关系数据库的通用机制，不绑定任何业务语义。模块说明「跨多个仓储的原子事务由工程级事务适配器包裹同一数据库上下文」说的正是这件事。</item>
    /// <item><b>受影响调用方与兼容策略</b>：纯新增，既有方法签名和语义一律不变；同一上下文里已经有事务时直接在那个事务里执行，不嵌套开新事务。</item>
    /// <item><b>数据与事务风险</b>：操作抛出异常时事务随释放回滚，已在内存里登记、尚未提交的改动仍留在当前工作单元，调用方应丢弃这个作用域。
    /// 事务的锁级别由数据库提供程序决定（SQLite 下 <c>BeginTransaction</c> 是立即写锁，其他写者排队等待）。</item>
    /// <item><b>验证命令</b>：<c>dotnet build HouseholdRelay.sln -c Release</c>；<c>tests/Tests</c> 的并发写入与人数上限用例。</item>
    /// </list>
    /// </remarks>
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation);

    /// <summary>
    /// 在一段操作期间把当前数据库上下文等锁、等命令的上限设成 <paramref name="lockWait"/>，操作结束（成功或抛出）后还原。
    /// </summary>
    /// <typeparam name="TResult">操作的返回值类型。</typeparam>
    /// <param name="lockWait">这段操作里每条命令（含开事务）最多等多久；不足一秒按一秒算。</param>
    /// <param name="operation">要执行的操作；其中的查询、提交和 <see cref="ExecuteInTransactionAsync" /> 都受这个上限约束。</param>
    /// <returns>返回操作的结果。</returns>
    /// <remarks>
    /// <b>[REPOSITORY-PARAM-003] 新增能力登记（HouseholdRelay 本工程扩展，中康原版没有）。</b>
    /// <list type="bullet">
    /// <item><b>通用搬运缺口</b>：上下文只有连接串给的一个默认等待时长，不同操作对「库被别人占着时等多久」要求不同：转发路径上的短写等久了会拖住请求，
    /// 维护操作要在截止时间前立刻得知库正忙；现有合同没有按操作调整等待上限的入口。</item>
    /// <item><b>应用层为何无法解决</b>：等待发生在数据库提供程序内部（SQLite 的忙重试），应用层拿不到连接与命令，事后取消也打断不了已在等锁的同步调用。</item>
    /// <item><b>复用场景</b>：用量写入（1 秒）、用量查询（2 秒）、站点身份换号与完成（1 秒）；命令超时是关系数据库的通用设置，不绑定业务语义。</item>
    /// <item><b>受影响调用方与兼容策略</b>：纯新增，不调用它的操作照旧按连接串的默认等待；需要关系型提供程序（非关系型提供程序上调用会抛出）。</item>
    /// <item><b>数据与事务风险</b>：只改等待上限，不改事务与提交语义；超时按提供程序的忙错误抛出，由调用方读成「库正忙」。</item>
    /// <item><b>验证命令</b>：<c>tests/Tests</c> 的站点维护「写锁被占时立刻失败」与用量忙写用例。</item>
    /// </list>
    /// </remarks>
    Task<TResult> WithLockWaitAsync<TResult>(TimeSpan lockWait, Func<Task<TResult>> operation);

    #endregion
}
