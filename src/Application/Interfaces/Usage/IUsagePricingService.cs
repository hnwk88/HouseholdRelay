using Core.Usage;

namespace Application.Interfaces.Usage;

/// <summary>按已保存上游（主键）和模型维护费率；发送时复制快照，之后改价不改变历史金额。</summary>
public interface IUsagePricingService
{
    /// <summary>列出某个已保存上游下的全部单价。</summary>
    IReadOnlyList<UsagePrice> List(string candidateId);

    /// <summary>校验模型名、币种和单价后保存到指定的已保存上游；未知单价留空。</summary>
    void Save(string candidateId, string model, string currency, decimal? input, decimal? output, decimal? read, decimal? write);
}
