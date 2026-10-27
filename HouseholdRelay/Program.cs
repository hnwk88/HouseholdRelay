using System.Text;

namespace HouseholdRelay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0) { Help(); return 0; }
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
            var management = new Management(Option("--home") ?? AppContext.BaseDirectory);
            switch (args[0])
            {
                case "init":
                    await management.Initialize(Option("--host") ?? throw new InvalidOperationException("请用 --host 指定花生壳域名。"), Port("--public-port", 5433), Port("--listen-port", 8787)); break;
                case "account": management.SetAccount(); break;
                case "serve": await management.Serve(); break;
                case "clients": management.ManageClient(args.ElementAtOrDefault(1) ?? "list", Option("--name")); break;
                case "client":
                    var split = Array.IndexOf(args, "--");
                    return await ClientLauncher.Run(Path.GetFullPath(Option("--bundle") ?? AppContext.BaseDirectory), split >= 0 ? args[(split + 1)..] : []);
                default: Help(); return 1;
            }
            return 0;
        }
        catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); return 1; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Console.Error.WriteLine("文件、Windows 凭据或端口操作失败。请检查路径、当前 Windows 用户及端口占用。"); return 1; }
        catch (Exception) { Console.Error.WriteLine("操作失败。请检查配置文件格式及 .NET 10 运行环境。"); return 1; }
    }
    private static void Help() => Console.WriteLine("""
        Household Relay — C# 单账号中转
        init --host 你的花生壳域名 [--public-port 5433] [--listen-port 8787]
        account                              隐藏输入并加密保存上游令牌
        serve                                启动 HTTPS 中转
        clients list                         查看客户端
        clients add|rotate|disable|enable --name office
        client [--bundle 文件夹] -- [Claude Code 参数]
        主机命令可用 --home 文件夹指定数据目录；默认为程序所在目录。
        """);
}
