using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>连接池读取活动上游版本的窄接口；正式服务读数据库，独立中转模式使用固定快照。</summary>
public interface IActiveUpstreamSource
{
    /// <summary>读取当前活动上游快照（含解密后的凭据）。</summary>
    UpstreamSnapshot ReadActive();

    /// <summary>读取在用那一家以外、测试通过的已保存上游快照（含凭据），按上游页的顺序；自动切换时按需读取，独立中转模式没有备用上游。</summary>
    IReadOnlyList<UpstreamSnapshot> ReadStandby();
}
