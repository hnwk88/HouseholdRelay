using System.Text;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Relay;
using HouseholdRelay.Commands;
using HouseholdRelay.Composition;
using HouseholdRelay.Management;
using HouseholdRelay.Relay;

namespace HouseholdRelay;

/// <summary>命令行入口：解析参数后经组合根构造容器，再从容器取各子命令的依赖。</summary>
public static class Program
{
    /// <summary>进程入口：无参数时默认 serve；按首个参数分派 init、account、serve、clients、client 子命令，--home 指定数据目录。返回 0 表示成功、1 表示参数或运行失败；所有已知异常都在这里转成中文提示，不把堆栈或敏感细节打到控制台。</summary>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0) args = ["serve"];
            string? Option(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
            int Port(string name, int fallback)
            {
                var text = Option(name);
                if (text is null) return fallback;
                return int.TryParse(text, out var port) ? port : throw new InvalidOperationException("端口必须是整数。");
            }
            var home = Path.GetFullPath(Option("--home") ?? AppContext.BaseDirectory);
            switch (args[0])
            {
                case "init":
                {
                    // 参数先于数据目录校验，缺少 --host 时不触碰数据目录。
                    var host = Option("--host") ?? throw new InvalidOperationException("请用 --host 指定花生壳域名。");
                    var publicPort = Port("--public-port", 5433); var listenPort = Port("--listen-port", 8787);
                    using var services = CompositionRoot.BuildCommandServices(home);
                    await services.GetRequiredService<ManagementCommands>().Initialize(host, publicPort, listenPort); break;
                }
                case "account":
                {
                    using var services = CompositionRoot.BuildCommandServices(home);
                    services.GetRequiredService<ManagementCommands>().SetAccount(); break;
                }
                case "serve":
                {
                    var adminPort = Port("--admin-port", 8788);
                    ServeCommand.CheckPort(adminPort);
                    await using var admin = ManagementWeb.Build(home, adminPort, services => services.AddSingleton<IRelayEventLog, ConsoleRelayEventLog>());
                    await admin.Services.GetRequiredService<ServeCommand>().Run(admin, adminPort); break;
                }
                case "clients":
                {
                    using var services = CompositionRoot.BuildCommandServices(home);
                    services.GetRequiredService<ManagementCommands>().ManageClient(args.ElementAtOrDefault(1) ?? "list", Option("--name"), Option("--id"), Option("--display-name")); break;
                }
                case "client":
                {
                    var split = Array.IndexOf(args, "--");
                    var bundle = Path.GetFullPath(Option("--bundle") ?? AppContext.BaseDirectory);
                    using var services = CompositionRoot.BuildCommandServices(home);
                    return await services.GetRequiredService<IClientLauncher>().Run(bundle, split >= 0 ? args[(split + 1)..] : []);
                }
                default: Help(); return 1;
            }
            return 0;
        }
        // 本程序把参数错误、业务拒绝统一抛成 InvalidOperationException，消息本身就是写给操作者的中文提示，可以原样输出。
        catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); return 1; }
        // 数据库失败只输出错误码，不输出 SQL 或路径细节；并明确告知不会因此重新开放首次设置，避免误以为可借故障重置管理员。
        catch (Microsoft.Data.Sqlite.SqliteException error)
        { Console.Error.WriteLine($"数据库操作失败（SQLite {error.SqliteErrorCode}）。请检查数据目录权限、文件占用及备份完整性；不会重新开放首次设置。"); return 1; }
        // 文件、Windows 凭据、端口和加解密失败的原始消息可能带路径或密钥信息，这里只给归类后的排查方向。
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Console.Error.WriteLine("文件、Windows 凭据或端口操作失败。请检查路径、当前 Windows 用户及端口占用。"); return 1; }
        // 兜底：未预料的异常不打印消息和堆栈，防止泄露令牌或配置内容，只返回失败码。
        catch (Exception) { Console.Error.WriteLine("操作失败。请检查配置文件格式及 .NET 10 运行环境。"); return 1; }
    }
    /// <summary>未知子命令时打印用法说明；由 <see cref="Main"/> 的默认分支调用，随后进程返回 1。</summary>
    private static void Help() => Console.WriteLine("""
        Household Relay — C# 单账号中转
        init --host 你的花生壳域名 [--public-port 5433] [--listen-port 8787]
        account                              隐藏输入并加密保存上游令牌
        serve [--admin-port 8788]             启动本机管理页与已配置的 HTTPS 中转
        clients list                         查看客户端
        clients add|rotate|disable|enable --name office
        clients rename --id 使用者主键 --display-name 显示名
        client [--bundle 文件夹] -- [Claude Code 参数]
        主机命令可用 --home 文件夹指定数据目录；默认为程序所在目录。
        """);
}
