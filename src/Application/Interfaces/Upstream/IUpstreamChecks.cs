using Application.Contracts.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>
/// 后台检测：页面提交只建任务、拿回检测编号，网络等待在后台做；页面按编号查状态、可以取消。只服务获取模型与测试这两件事，不是通用任务框架。
/// 同一家同时只能有一次检测；检测只在内存里，服务重启后库里残留的「检测中」由启动恢复写成「检测未完成」。
/// </summary>
public interface IUpstreamChecks
{
    /// <summary>在后台获取这家的模型目录，返回检测编号；结束后的读句是 <paramref name="lead"/> 加目录读句。这家正在检测时拒绝。</summary>
    /// <param name="upstreamCandidateId">已保存上游主键。</param>
    /// <param name="lead">结论前面加的一句（例如「已保存「某某」。」），可以为空串。</param>
    long StartDiscovery(long upstreamCandidateId, string lead);

    /// <summary>在后台对这家做一次最小模型调用测试（按钮已写明会花少量额度，按下即确认），返回检测编号。这家正在检测时拒绝。</summary>
    /// <param name="upstreamCandidateId">已保存上游主键。</param>
    long StartTest(long upstreamCandidateId);

    /// <summary>按检测编号读此刻的状态；编号不认识（进程重启过、或早已被更新的记录挤掉）时为空。</summary>
    UpstreamCheck? Read(long checkId);

    /// <summary>这家此刻正在进行的那次检测；没有时为空。</summary>
    UpstreamCheck? Running(long upstreamCandidateId);

    /// <summary>取消一次正在进行的检测；已结束或编号不认识时返回 false，什么都不做。</summary>
    bool Cancel(long checkId);
}
