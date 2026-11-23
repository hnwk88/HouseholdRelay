using Application.Contracts.Upstream;
using Application.Interfaces.Upstream;
using Application.Logging;
using Core.Upstream;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Services.Upstream;

/// <summary>
/// 后台检测：获取模型与测试在后台跑，页面只拿检测编号、查状态、可取消。检测本身仍由 <see cref="IUpstreamService.DiscoverModelsAsync"/>、<see cref="IUpstreamService.TestCandidateAsync"/> 做，
/// 「检测中→结论／检测未完成」的收尾在那两处；这里只负责调度、记下读句、接住所有异常（意外的记一行日志）。
/// 作为后台服务启动时把库里残留的「检测中」写成「检测未完成」，停止时取消进行中的检测并等它们收尾。记录只在内存里，最多留最近 <see cref="Kept"/> 次。
/// </summary>
public sealed class UpstreamChecks : IUpstreamChecks, IHostedService, IDisposable
{
    /// <summary>内存里最多留几次检测的记录（进行中的不会被挤掉）。</summary>
    public const int Kept = 20;

    /// <summary>获取模型进行中时的读句。</summary>
    private const string RunningDiscovery = "正在获取模型，结果出来会自动读出。";

    /// <summary>测试进行中时的读句。</summary>
    private const string RunningTest = "正在测试，结果出来会自动读出。";

    private readonly IUpstreamService _upstreams;
    private readonly ILogger<UpstreamChecks> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, Job> _jobs = [];
    private long _next;

    /// <summary>两个依赖都必填，构造时当场判空。</summary>
    public UpstreamChecks(IUpstreamService upstreams, ILogger<UpstreamChecks> logger)
    {
        ArgumentNullException.ThrowIfNull(upstreams); ArgumentNullException.ThrowIfNull(logger);
        _upstreams = upstreams; _logger = logger;
    }

    /// <summary>在后台获取这家的模型目录，返回检测编号；结束后的读句是 <paramref name="lead"/> 加目录读句。这家正在检测时拒绝。</summary>
    public long StartDiscovery(long upstreamCandidateId, string lead) => Start(upstreamCandidateId, UpstreamCheckKind.Discovery, lead);

    /// <summary>在后台对这家做一次最小模型调用测试，返回检测编号。这家正在检测时拒绝。</summary>
    public long StartTest(long upstreamCandidateId) => Start(upstreamCandidateId, UpstreamCheckKind.Test, "");

    /// <summary>按检测编号读此刻的状态；编号不认识时为空。</summary>
    public UpstreamCheck? Read(long checkId)
    {
        // 记录的状态与读句由后台任务在同一把锁里改，读快照也要持锁，读不到改了一半的记录。
        lock (_gate) return _jobs.TryGetValue(checkId, out var job) ? job.Snapshot() : null;
    }

    /// <summary>这家此刻正在进行的那次检测；没有时为空。</summary>
    public UpstreamCheck? Running(long upstreamCandidateId)
    {
        // 同上：持锁遍历，后台任务结束、新检测登记都不会在遍历途中改动字典。
        lock (_gate) return _jobs.Values.FirstOrDefault(job => job.UpstreamCandidateId == upstreamCandidateId && job.Status == UpstreamCheckStatus.Running)?.Snapshot();
    }

    /// <summary>取消一次正在进行的检测；已结束或编号不认识时返回 false，什么都不做。</summary>
    public bool Cancel(long checkId)
    {
        // 判状态与取消在同一把锁里：已结束的检测不会被误取消，取消令牌也不会在释放之后才被用到。
        lock (_gate)
        {
            if (!_jobs.TryGetValue(checkId, out var job) || job.Status != UpstreamCheckStatus.Running) return false;
            // 只发出取消，不等：后台任务自己按「检测未完成」收尾，页面按编号读到结束。
            job.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>服务启动：把库里残留的「检测中」写成「检测未完成」；库这时读不出来只记一行日志，不挡住启动。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await _upstreams.RecoverInterruptedChecksAsync(); }
        // 恢复失败不让服务起不来：残留的那几家过了检测窗口照样读作「检测未完成」。
        catch (Exception error) when (error is not OperationCanceledException) { RelayLog.Unexpected(_logger, error, "upstream-check:recover"); }
    }

    /// <summary>服务停止：取消进行中的检测，等它们写完「检测未完成」再返回（最多等到停止令牌取消）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 停止令牌连着每次检测自己的令牌：一处取消，进行中的检测都收尾成「检测未完成」。
        await _stopping.CancelAsync();
        Task[] running;
        // 持锁取出后台任务再在锁外等：等待期间后台任务要进锁写结论，不能占着锁等它。
        lock (_gate) running = _jobs.Values.Select(job => job.Work).Where(task => task is not null).Select(task => task!).ToArray();
        try { await Task.WhenAll(running).WaitAsync(cancellationToken); }
        // 宿主等不及了：剩下的检测随进程结束，库里停在检测中的下次启动时写成「检测未完成」。
        catch (OperationCanceledException) { }
    }

    /// <summary>释放停止令牌源与已结束检测的令牌源。</summary>
    public void Dispose()
    {
        // 只释放已结束的：进行中的那几次还在用自己的令牌，随后台任务一起结束。
        lock (_gate) foreach (var job in _jobs.Values) if (job.Status != UpstreamCheckStatus.Running) job.Cancellation.Dispose();
        _stopping.Dispose();
    }

    /// <summary>建一次检测并在后台跑：这家已有进行中的检测时拒绝；留着的记录超过 <see cref="Kept"/> 次时挤掉最早结束的。</summary>
    private long Start(long upstreamCandidateId, UpstreamCheckKind kind, string lead)
    {
        Job job;
        // 判「这家有没有在检测」与登记新检测在同一把锁里：同一家连按两下只会有一次检测。
        lock (_gate)
        {
            if (_jobs.Values.Any(item => item.UpstreamCandidateId == upstreamCandidateId && item.Status == UpstreamCheckStatus.Running))
                throw new InvalidOperationException("这个上游正在检测，请等这次检测结束，或先按「取消检测」。");
            // 每次检测一个自己的令牌源，连着服务停止令牌：取消只停这一次，服务停止时一起停。
            job = new Job(++_next, upstreamCandidateId, kind, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token),
                kind == UpstreamCheckKind.Discovery ? RunningDiscovery : RunningTest);
            _jobs[job.Id] = job;
            foreach (var old in _jobs.Values.Where(item => item.Status != UpstreamCheckStatus.Running).OrderBy(item => item.Id).Take(Math.Max(0, _jobs.Count - Kept)).ToArray())
            { _jobs.Remove(old.Id); old.Cancellation.Dispose(); }
        }
        // 网络等待不占页面请求：放到线程池上跑，页面马上拿回编号。
        job.Work = Task.Run(() => RunAsync(job, lead));
        return job.Id;
    }

    /// <summary>跑一次检测并记下结论或原因；所有异常都在这里接住，后台任务不会以异常结束。</summary>
    private async Task RunAsync(Job job, string lead)
    {
        // 这次检测自己的令牌：管理员按取消或服务停止时取消，传给检测服务，网络请求与子进程随之停下。
        var token = job.Cancellation.Token;
        try
        {
            var message = job.Kind == UpstreamCheckKind.Discovery
                ? lead + (await _upstreams.DiscoverModelsAsync(job.UpstreamCandidateId, token)).Message
                : (await _upstreams.TestCandidateAsync(job.UpstreamCandidateId, true, token)).Message;
            // 测试被取消时检测服务照旧返回一份「已取消」报告、不抛出；按令牌判：取消了就读「检测未完成」，与获取模型被取消读同一句。
            if (token.IsCancellationRequested) End(job, UpstreamCheckStatus.Cancelled, UpstreamReport.UnfinishedLead + UpstreamReport.CancelledReason + UpstreamReport.UnfinishedNext);
            else End(job, UpstreamCheckStatus.Finished, message);
        }
        // 管理员取消或服务停止：那一家已由服务写成「检测未完成」，这里读出同一句。
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { End(job, UpstreamCheckStatus.Cancelled, UpstreamReport.UnfinishedLead + UpstreamReport.CancelledReason + UpstreamReport.UnfinishedNext); }
        // 业务拒绝（正在检测、登录凭据不在、取不到账户编号等）：读句本身是给管理员的中文说明。
        catch (InvalidOperationException error) { End(job, UpstreamCheckStatus.Failed, error.Message); }
        // 没有预料到的故障：记一行日志（只有类型链、阶段、上游主键），读出「检测未完成」与下一步。
        catch (Exception error)
        {
            RelayLog.Unexpected(_logger, error, job.Kind == UpstreamCheckKind.Discovery ? "upstream-check:discovery" : "upstream-check:test", upstreamCandidateId: job.UpstreamCandidateId);
            End(job, UpstreamCheckStatus.Failed, UpstreamReport.UnfinishedLead + UpstreamReport.FaultReason + UpstreamReport.UnfinishedNext);
        }
    }

    /// <summary>把检测记为结束。</summary>
    private void End(Job job, UpstreamCheckStatus status, string message)
    {
        // 状态与读句一起改，读方持同一把锁，不会读到「已结束」却还是进行中的读句。
        lock (_gate) { job.Status = status; job.Message = message; }
    }

    /// <summary>一次检测的内存记录；可变字段只在 <see cref="_gate"/> 锁里读写。</summary>
    private sealed class Job(long id, long upstreamCandidateId, UpstreamCheckKind kind, CancellationTokenSource cancellation, string message)
    {
        /// <summary>检测编号。</summary>
        public long Id { get; } = id;

        /// <summary>已保存上游主键。</summary>
        public long UpstreamCandidateId { get; } = upstreamCandidateId;

        /// <summary>做哪件事。</summary>
        public UpstreamCheckKind Kind { get; } = kind;

        /// <summary>这次检测自己的取消令牌源（随服务停止一起取消）。</summary>
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>走到哪。</summary>
        public UpstreamCheckStatus Status { get; set; } = UpstreamCheckStatus.Running;

        /// <summary>读句。</summary>
        public string Message { get; set; } = message;

        /// <summary>后台任务；建好后马上赋值。</summary>
        public Task? Work { get; set; }

        /// <summary>给调用方的快照。</summary>
        public UpstreamCheck Snapshot() => new(Id, UpstreamCandidateId, Kind, Status, Message);
    }
}
