using Application.Contracts.Upstream;
using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>网页和命令行共用的上游保存、获取模型、测试、改名和原子切换服务。</summary>
public interface IUpstreamService : IActiveUpstreamSource
{
    /// <summary>最近保存的上游；一个都没有时为空。</summary>
    Task<UpstreamCandidateView?> LatestCandidate();

    /// <summary>按创建先后列出全部已保存上游，不含秘密。</summary>
    Task<IReadOnlyList<UpstreamCandidateView>> ListCandidates();

    /// <summary>按主键读取已保存上游；不存在时提示重新加载页面。</summary>
    Task<UpstreamCandidateView> ReadCandidate(long id);

    /// <summary>校验并加密保存一个带名称的上游，返回主键；<paramref name="name"/> 为空时用地址主机名（命令行用），空白名称拒绝；活动版本已变化时拒绝。</summary>
    Task<long> SaveCandidate(long expectedVersion, UpstreamOptions options, string credential, string? name);

    /// <summary>同上，名称用地址的主机名。</summary>
    Task<long> SaveCandidate(long expectedVersion, UpstreamOptions options, string credential) => SaveCandidate(expectedVersion, options, credential, null);

    /// <summary>
    /// 登录官方账户并保存为一个账户模式的未测试上游，返回主键：<paramref name="options"/> 给出地址与私网开关，凭据由 <c>claude auth login</c> 写进这个上游自己的配置目录；
    /// <paramref name="name"/> 为空时用登录邮箱。登录失败或活动版本已变化时抛出 <see cref="InvalidOperationException"/> 读出原因。
    /// </summary>
    Task<long> LoginOfficial(long expectedVersion, UpstreamOptions options, string? name, CancellationToken cancellation);

    /// <summary>
    /// 对一家登录得到的官方账户重新登录（凭据失效、续期不了时用）：在临时目录里委托本机 Claude Code 登录，核对登的是不是原来那个账户（账户编号相同；从没测试过、还没有编号的按同一个接上），
    /// 是就把新凭据换进这一家的账户目录，主键、设备编号、账户资料和思域绑定都不变，返回登录邮箱（取不到为 null）；登成了别的账户就拒绝，原凭据不动。
    /// 不是登录得到的官方账户、已导出搬走、配置损坏、登录取消或失败时抛出 <see cref="InvalidOperationException"/>（消息是读给管理员的原因与下一步）。临时目录无论成败都删掉。
    /// </summary>
    Task<string?> Relogin(long id, CancellationToken cancellation);

    /// <summary>
    /// 导入别处 Claude Code 登录成功后生成的 <c>.credentials.json</c>（<paramref name="content"/>）并保存为一个账户模式的未测试上游，返回主键；此后与 <see cref="LoginOfficial"/> 得到的上游完全一样。
    /// <paramref name="name"/> 为空时用登录邮箱；文件不合格、未安装 Claude Code、名称不合规则或活动版本已变化时抛出 <see cref="InvalidOperationException"/> 读出原因，不新增上游、不留账户目录。
    /// </summary>
    Task<long> ImportOfficial(long expectedVersion, UpstreamOptions options, byte[] content, string? name, CancellationToken cancellation);

    /// <summary>
    /// 把 <paramref name="ids"/> 指定的上游按上游页顺序导出成用 <paramref name="password"/>（12–128 个字符，不保存）加密的文件；登录得到的官方账户随之搬走：
    /// 本实例此后不再用它转发、不再续期，账户目录留着等管理员删除。密码不合规则、一家都没勾或勾的已不在时抛出 <see cref="InvalidOperationException"/>，什么都不改。
    /// </summary>
    Task<UpstreamExport> Export(IReadOnlyCollection<long> ids, string password);

    /// <summary>
    /// 用 <paramref name="password"/> 解开 <see cref="Export"/> 产生的文件，按文件里的顺序把每家加成未测试上游（与「API 供应商」保存、「导入登录文件」导入的完全一样），排在已有上游后面。
    /// 文件或密码不对、活动版本已变化时抛出 <see cref="InvalidOperationException"/> 读出原因，一家都不写；解开以后个别上游被拒只记在它自己的结果里。
    /// </summary>
    Task<IReadOnlyList<UpstreamImportOutcome>> Import(long expectedVersion, byte[] file, string password, CancellationToken cancellation);

    /// <summary>读取活动上游的非秘密部分供页面展示；不读凭据、不等官方登录令牌刷新。转发用 <see cref="IActiveUpstreamSource.ReadActive"/>。</summary>
    Task<UpstreamOverview> ReadOverview();

    /// <summary>打开或关闭自动切换，保存即生效；关闭后只用在用的那一家，行为与没有自动切换时相同。</summary>
    Task SetFailover(bool enabled);

    /// <summary>修改供应商名称；只影响显示，切换和用量都按主键关联。</summary>
    Task Rename(long id, string name);

    /// <summary>
    /// 删除一个已保存上游：停用而不是抹掉——它从上游页、切换和导出里消失，密钥清掉（官方登录的连同账户目录），用量历史照常保留并读出它的名字。
    /// 当前使用中或正在检测时拒绝，读出原因与下一步；返回仍引用它的用量、尝试和单价条数，供页面读给管理员。
    /// </summary>
    Task<UpstreamReferences> Delete(long id);

    /// <summary>获取模型目录（不花额度），自动探测的认证方式写回该上游；手动指定的认证方式不被覆盖。当前使用中的上游保留原测试结论，只更新模型目录。</summary>
    Task<ModelCatalog> DiscoverModels(long id, CancellationToken cancellation);

    /// <summary>选定要测试的模型；与原值不同时清除旧的通过状态，需重新测试才能切换。</summary>
    Task ChooseModel(long id, string model);

    /// <summary>经操作者确认后对上游做一次最小模型调用检测，保存安全的检测报告并写回自动探测出的认证方式与检测方式。标记检测中之后出错或被取消时写成「检测未完成」再抛出。</summary>
    Task<UpstreamReport> TestCandidate(long id, bool confirmed, CancellationToken cancellation);

    /// <summary>服务启动时把库里残留的「检测中」写成「检测未完成」（检测任务只在内存里，重启后没人再写完它们），返回改了几家。</summary>
    Task<int> RecoverInterruptedChecks();

    /// <summary>把检测通过的上游原子切换为活动版本；<paramref name="expectedVersion"/> 是调用方看到的活动版本，为空时沿用该上游保存时基于的版本。</summary>
    Task Activate(long id, long expectedVersion);

    /// <summary>同上，活动版本按这家保存时基于的那个版本核对。</summary>
    Task Activate(long id);
}
