namespace Repositories;

/// <summary>
/// 定义数据库上下文对显式物理删除实体的跟踪能力。
/// </summary>
/// <remarks>
/// 某些工程会在 <c>SaveChanges</c> 中把所有 <c>Deleted</c> 状态自动转换成软删除。
/// 通用仓储执行物理删除前通过本接口登记实体，使上下文能够区分“普通删除请求”和“已经由应用层明确授权的物理删除”。
/// </remarks>
public interface IHardDeleteTracker
{
    /// <summary>
    /// 登记一个已经明确要求物理删除的实体实例。
    /// </summary>
    /// <param name="entity">准备物理删除的实体实例。</param>
    void MarkHardDelete(object entity);

    /// <summary>
    /// 判断指定实体是否已经登记为物理删除。
    /// </summary>
    /// <param name="entity">待判断的实体实例。</param>
    /// <returns>已经登记返回 <see langword="true" />，否则返回 <see langword="false" />。</returns>
    bool IsHardDelete(object entity);

    /// <summary>
    /// 在数据库提交成功后清除本工作单元的物理删除标记。
    /// </summary>
    void ClearHardDeleteMarks();
}
