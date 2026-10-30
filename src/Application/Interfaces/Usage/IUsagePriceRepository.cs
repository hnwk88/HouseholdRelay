using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>按已保存上游主键和模型保存的单价表的持久化。</summary>
public interface IUsagePriceRepository
{
    /// <summary>列出某个已保存上游下的全部单价，按模型排序。</summary>
    IReadOnlyList<UsagePrice> List(string candidateId);

    /// <summary>单价所属的已保存上游仍存在时写入或覆盖该模型单价，否则提示刷新。</summary>
    void Save(UsagePrice price);
}
