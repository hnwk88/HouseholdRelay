using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>按上游版本和模型保存的单价表的持久化。</summary>
public interface IUsagePriceRepository
{
    /// <summary>列出某个上游版本下的全部单价，按模型排序。</summary>
    IReadOnlyList<UsagePrice> List(long version);

    /// <summary>活动上游版本仍等于单价所属版本时写入或覆盖该模型单价，否则提示刷新。</summary>
    void Save(UsagePrice price);
}
