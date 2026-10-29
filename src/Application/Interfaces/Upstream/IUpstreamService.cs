using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>网页和命令行共用的上游保存、获取模型、测试、改名和原子切换服务。</summary>
public interface IUpstreamService : IActiveUpstreamSource
{
    /// <summary>最近保存的上游；一个都没有时为空。</summary>
    UpstreamCandidate? LatestCandidate();

    /// <summary>按创建先后列出全部已保存上游，不含秘密。</summary>
    IReadOnlyList<UpstreamCandidate> ListCandidates();

    /// <summary>按主键读取已保存上游；不存在时提示重新加载页面。</summary>
    UpstreamCandidate ReadCandidate(string id);

    /// <summary>校验并加密保存一个带名称的上游，返回主键；<paramref name="name"/> 为空时用地址主机名（命令行用），空白名称拒绝；活动版本已变化时拒绝。</summary>
    string SaveCandidate(long expectedVersion, UpstreamOptions options, string credential, string? name = null);

    /// <summary>
    /// 登录官方账户并保存为一个账户模式的未测试上游，返回主键：<paramref name="options"/> 给出地址与私网开关，凭据由 <c>claude auth login</c> 写进这个上游自己的配置目录；
    /// <paramref name="name"/> 为空时用登录邮箱。登录失败或活动版本已变化时抛出 <see cref="InvalidOperationException"/> 读出原因。
    /// </summary>
    Task<string> LoginOfficial(long expectedVersion, UpstreamOptions options, string? name, CancellationToken cancellation);

    /// <summary>修改供应商名称；只影响显示，切换和用量都按主键关联。</summary>
    void Rename(string id, string name);

    /// <summary>删除一个已保存上游（官方登录的连同它的账户目录）；当前使用中或正在检测时拒绝，读出原因与下一步。</summary>
    void Delete(string id);

    /// <summary>获取模型目录（不花额度），自动探测的认证方式写回该上游；手动指定的认证方式不被覆盖。</summary>
    Task<ModelCatalog> DiscoverModels(string id, CancellationToken cancellation);

    /// <summary>选定要测试的模型；与原值不同时清除旧的通过状态，需重新测试才能切换。</summary>
    void ChooseModel(string id, string model);

    /// <summary>经操作者确认后对上游做一次最小模型调用检测，保存安全的检测报告并写回自动探测出的认证方式与检测方式。</summary>
    Task<UpstreamReport> TestCandidate(string id, bool confirmed, CancellationToken cancellation);

    /// <summary>把检测通过的上游原子切换为活动版本；<paramref name="expectedVersion"/> 是调用方看到的活动版本，为空时沿用该上游保存时基于的版本。</summary>
    void Activate(string id, long? expectedVersion = null);
}
