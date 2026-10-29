using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>全局出站代理的唯一解析入口：上游转发、获取模型、测试和 Claude Code 检测子进程都向它要代理。</summary>
public interface IUpstreamProxy
{
    /// <summary>按当前设置为目标地址解析代理；未启用时返回直连。已启用却取不到地址时抛出 <see cref="UpstreamProxyUnavailableException"/>，绝不悄悄直连。</summary>
    ProxyRoute Resolve(Uri target);

    /// <summary>给设置页读的一句话：当前方式与实际解析到的代理，或取不到代理的原因；不抛异常。</summary>
    string Describe();
}
