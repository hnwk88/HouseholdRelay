namespace Application.Contracts.Upstream;

/// <summary>后台检测做的是哪一件事：获取模型（不花额度）与测试（最小模型调用，可能花少量额度）是两件事，各有自己的入口。</summary>
public enum UpstreamCheckKind
{
    /// <summary>获取模型目录。</summary>
    Discovery,

    /// <summary>最小模型调用测试。</summary>
    Test,
}
