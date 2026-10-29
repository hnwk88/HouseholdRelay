using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>按活动上游版本和模型维护费率；发送时复制快照，之后改价不改变历史金额。</summary>
public interface IUsagePricingService
{
    /// <summary>列出某个上游版本下的全部单价。</summary>
    IReadOnlyList<UsagePrice> List(long version);

    /// <summary>校验模型名、币种和单价后保存；未知单价留空。</summary>
    void Save(long version, string model, string currency, decimal? input, decimal? output, decimal? read, decimal? write);
}
