namespace Application.Interfaces.Relay;

/// <summary>中转的唯一入口：鉴权、准入、用量记录、请求改写、上游转发与自动切换、错误分类与回写顺序都在这里，宿主只把请求包成 <see cref="IRelayExchange"/> 交进来。</summary>
public interface IRelayForwarder
{
    /// <summary>处理一次请求，直到回应写完或连接被中止；可预期的拒绝经 <see cref="IRelayExchange.FailAsync"/> 写回，不向外抛。</summary>
    Task HandleAsync(IRelayExchange exchange);
}
