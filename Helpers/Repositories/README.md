# 通用 EF Core 仓储、软删除和物理删除 Helper

## 用途

提供不绑定具体工程名称、业务表或 DbContext 名称的 BaseEntity、IRepository<T>、Repository<T> 和 AddRepositories<TDbContext>；完整覆盖单条/批量增改、条件/导航/分页查询、最新只读快照、包含软删除查询重载、软删除、物理删除、软硬删除开关重载、审计来源和集中 SaveChanges。物理删除通过 IHardDeleteTracker 显式登记，避免被工程 DbContext 的默认软删除转换静默改写。

## 依赖

- net10.0
- EF Core 10
- Microsoft.Extensions.DependencyInjection
- long 主键（`BaseEntity.Id`，由数据库生成；中康原版这里写的「Guid 主键」与 `BaseEntity` 不符，本工程这份已更正）
- 命名软删除查询过滤器
- 项目 DbContext 实现 IHardDeleteTracker

## 使用示例

```csharp
services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));
services.AddRepositories<AppDbContext>();

var activeItems = await repository.GetAllAsync();
var allItems = await repository.GetAllAsync(includeDeleted: true);

// false 表示软删除，true 表示已经由应用层确认的物理删除。
await repository.DeleteAsync(id, isHardDelete: false);
await repository.SaveChangesAsync();
            ```

## 与中康原版的差别（HouseholdRelay）

本目录 2026-11-20 从 `ZKMedicalSystem\Helpers\Repositories` 整目录复制进来（订单 151「数据三」），此后以本工程根目录 `Helpers/Repositories` 为主源。与中康原版只有下面四处不同，其余逐字相同：

1. **`Repositories.Abstractions.csproj` 补编译项 `RepositorySortKey.cs`**：`IRepository.cs` 的多键分页重载用到 `RepositorySortKey<TEntity>`，原版清单漏列，单独构建抽象项目报 CS0246（实测属实）。
2. **新增 `IRepository<TEntity>.ExecuteInTransactionAsync<TResult>`**（登记号 [REPOSITORY-PARAM-002]，理由写在接口注释里）：在同一数据库上下文的一个事务里「先读、判断、再写」，同一上下文已有事务时不嵌套。用于使用者人数上限、管理员首次设置、上游激活与停用、按版本保存设置、站点身份换号等跨行条件写；SQLite 下是立即写锁。
3. **「依赖」一节的主键类型**：原版写「Guid 主键」，与 `BaseEntity.Id`（long）不符，本份更正为 long。
4. **新增 `IRepository<TEntity>.WithLockWaitAsync<TResult>`**（登记号 [REPOSITORY-PARAM-003]，理由写在接口注释里）：在一次操作期间临时把上下文的命令超时设为给定的等锁上限，结束后恢复原值；用于用量写入（1 秒）、用量查询（2 秒）、站点身份换号与完成（1 秒）。为此 `Repositories.EntityFrameworkCore.csproj` 多引用 `Microsoft.EntityFrameworkCore.Relational` 10.0.10（`SetCommandTimeout` 在关系型扩展里）；非关系型提供程序上调用会抛出。

下面「维护来源」「同步规则」里的 `portable-rule-kit`、`tools/Docs.ps1 helpers-sync` 是中康工程的脚本，本工程没有；本工程改这份模块时直接改这里，并在本节追加差别。

## 维护来源

- 主工程源码：`Helpers/Repositories/IRepository.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/IRepository.cs`
- 主工程源码：`Helpers/Repositories/Repositories.Abstractions.csproj`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/Repositories.Abstractions.csproj`
- 主工程源码：`Helpers/Repositories/Repositories.EntityFrameworkCore.csproj`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/Repositories.EntityFrameworkCore.csproj`
- 主工程源码：`Helpers/Repositories/BaseEntity.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/BaseEntity.cs`
- 主工程源码：`Helpers/Repositories/DateTimeProvider.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/DateTimeProvider.cs`
- 主工程源码：`Helpers/Repositories/Directory.Build.props`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/Directory.Build.props`
- 主工程源码：`Helpers/Repositories/ICurrentUserService.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/ICurrentUserService.cs`
- 主工程源码：`Helpers/Repositories/IDateTimeProvider.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/IDateTimeProvider.cs`
- 主工程源码：`Helpers/Repositories/IHardDeleteTracker.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/IHardDeleteTracker.cs`
- 主工程源码：`Helpers/Repositories/NullCurrentUserService.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/NullCurrentUserService.cs`
- 主工程源码：`Helpers/Repositories/Repository.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/Repository.cs`
- 主工程源码：`Helpers/Repositories/RepositoryFilterNames.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/RepositoryFilterNames.cs`
- 主工程源码：`Helpers/Repositories/RepositoryServiceCollectionExtensions.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/RepositoryServiceCollectionExtensions.cs`
- 主工程源码：`Helpers/Repositories/RepositorySortKey.cs`
- 可移植副本：`portable-rule-kit/Helpers/Repositories/RepositorySortKey.cs`

## 同步规则

- 主工程代码修改后，必须运行 `tools/Docs.ps1 helpers-sync` 或 `py -3 tools/doc_maintainer.py helpers-sync`。
- 同步后必须运行 `tools/Docs.ps1 helpers-check` 或 `py -3 tools/doc_maintainer.py helpers-check`。
- 如果 Helper 行为变化，必须同步更新本说明的用途、依赖和使用示例。
- 可移植副本只用于迁移到其它工程；迁移后以工程根目录 `Helpers/` 作为 Helper 主源码目录，并从这里继续同步。


## 接入和数据安全边界

- 必须复制整个 `Repositories` 目录并同时保留两个项目文件；只复制 `IRepository.cs` 或 `Repository.cs` 会缺少实体基类、审计服务、硬删除跟踪和依赖注入注册，不能作为可用迁移结果。
- 只需要契约的项目引用 `Repositories.Abstractions.csproj`；需要 EF Core 实现和依赖注入注册的项目引用 `Repositories.EntityFrameworkCore.csproj`。
- 宿主 `DbContext` 必须实现 `IHardDeleteTracker`，并在默认软删除转换前识别已经登记的物理删除；否则 `isHardDelete=true` 会被静默改回软删除。
- `includeDeleted=true` 只能读取软删除记录，物理删除记录已经从数据库消失，任何查询重载都不能恢复。
- EF Core 10 宿主只按名称关闭 `RepositoryFilterNames.SoftDeletion`；租户、诊所站点和其它安全过滤器必须继续生效。
- `GetByIdSnapshotAsync` 用 `AsNoTracking` 重新物化最新只读事实，适合任务终态等“双检”场景；它始终保留软删除和宿主安全过滤器，返回对象不得用于写回。
- Application 负责权限、引用门禁、删除原因和删除范围；仓储只执行已经决定的数据搬运、审计字段和固定关系完整性，禁止把审核归宿、翻译生效或网络结论下沉到通用仓储。
- `helpers-sync` 会扫描项目型 Helper 目录和 MSBuild 本地依赖；`helpers-check` 会阻断缺失依赖、越界依赖和无条件外部 Import。修改后仍需从可移植副本执行 `dotnet restore` 与 Release 构建，证明迁移单元可独立使用。

