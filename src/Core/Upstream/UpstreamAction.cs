namespace Core.Upstream;

/// <summary>上游页里一家上游的操作菜单能列的事；哪一家此刻能做哪几件由 <see cref="UpstreamCandidateView.Actions"/> 给出，页面只把取值读成文字和地址。</summary>
public enum UpstreamAction
{
    /// <summary>切换到这一家：只给测试通过、不是在用、没搬走、配置完好、不在检测中的那一家。</summary>
    Activate,

    /// <summary>查看这一家的用量：跳到用量页，只看这一家记账的请求。</summary>
    ViewUsage,

    /// <summary>重新登录：只给登录得到、没搬走、不在检测中的官方账户；换上新凭据，这一家的主键与思域绑定不变。</summary>
    Relogin,

    /// <summary>选择模型并测试（会消耗少量上游额度）。</summary>
    Test,

    /// <summary>重新获取模型目录（不花额度）。</summary>
    Rediscover,

    /// <summary>改名：只改显示名称。</summary>
    Rename,

    /// <summary>导出：到搬家一节，只预选这一家。</summary>
    Export,

    /// <summary>删除（停用）：读 <see cref="UpstreamCandidateView.Deletable"/>，与删除时同一判据。</summary>
    Delete,
}
