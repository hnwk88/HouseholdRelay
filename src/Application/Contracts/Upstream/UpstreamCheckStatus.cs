namespace Application.Contracts.Upstream;

/// <summary>一次后台检测此刻走到哪：进行中，或三种结束之一。只在内存里，不进库。</summary>
public enum UpstreamCheckStatus
{
    /// <summary>进行中。</summary>
    Running,

    /// <summary>走到了结论（获取到目录，或测试得出通过、未通过）；读句就是结论。</summary>
    Finished,

    /// <summary>没走到结论就被拒或出错；那一家已写成「检测未完成」或保持原样，读句是原因与下一步。</summary>
    Failed,

    /// <summary>管理员取消了；那一家已写成「检测未完成」。</summary>
    Cancelled,
}
