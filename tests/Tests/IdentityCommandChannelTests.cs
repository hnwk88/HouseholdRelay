using Application.Interfaces.Identity;
using Application.Interfaces.Usage;
using Core.Identity;
using HouseholdRelay.Commands;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>命令行改使用者时与运行中服务的协调：服务在运行就经本机管道交给它执行，管道不通就不保存。</summary>
public static class IdentityCommandChannelTests
{
    /// <summary>宿主在运行：命令行轮换令牌由宿主执行，命令返回前宿主的令牌名单已替换并通知订阅者（思域会话据此当场关闭）。</summary>
    public static async Task RunningServiceExecutesClientCommands()
    {
        await using var web = await WebFixture.Create();
        var identities = web.App.Services.GetRequiredService<IIdentityService>();
        var issued = await identities.Create("书房");
        var notifications = new List<IReadOnlyList<ClientAccess>>();
        // 通知在宿主的管道线程上回调，记录与读取都加锁。
        web.App.Services.GetRequiredService<ClientRegistry>().Subscribe(list => { lock (notifications) notifications.Add(list); });

        var cli = new TestProcess(web.Home.Path);
        await cli.Get<ManagementCommands>().ManageClient("rotate", null, issued.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        IReadOnlyList<ClientAccess> last;
        // 与回调同锁读取最后一次通知。
        lock (notifications) last = notifications.LastOrDefault() ?? [];
        Check(last.Count > 0 && last.Single(c => c.UserId == issued.UserId).TokenVersion == 2, "命令返回前宿主名单已换成第 2 版并通知了订阅者");
        Check(await web.App.Services.GetRequiredService<ClientRegistry>().Authenticate(issued.Token) is null, "旧令牌在宿主里立即失效");
    }

    /// <summary>服务锁被占着（服务在运行）却连不上管道：拒绝并说清原因，不落库。</summary>
    public static async Task UnreachableServiceRefusesWithoutSaving()
    {
        var root = Path.Combine(Path.GetTempPath(), "household-relay-csharp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var holder = new TestProcess(root);
            var issued = await holder.Identities.Create("卧室");
            using var serving = holder.Get<IServingLock>().Acquire();
            var cli = new TestProcess(root);
            var refused = false;
            try { await cli.Get<ManagementCommands>().ManageClient("disable", null, issued.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            // 预期的拒绝：读句说明没有保存。
            catch (InvalidOperationException error) { refused = error.Message.Contains("没有保存", StringComparison.Ordinal); }
            Check(refused, "连不上运行中服务的管道应拒绝并说明没有保存");
            Check((await cli.Identities.ReadAccess()).Single(c => c.UserId == issued.UserId).Enabled, "拒绝后使用者仍是启用");
        }
        // 临时数据目录用完即删，失败也删：先清连接池放开库文件；删不掉只留一个临时目录，不掩盖用例结果。
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            // 文件仍被占用：放弃清理。
            catch (IOException) { }
        }
    }
}
