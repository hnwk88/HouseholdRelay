using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;
using Core.Configuration;
using Core.Identity;

namespace HouseholdRelay.Commands;

/// <summary>命令行 init、account、clients 子命令；依赖由容器注入，不再自行装配服务。</summary>
public sealed class ManagementCommands(IConfigurationService settings, IIdentityService identities)
{
    /// <summary>读取当前配置，并把身份库里的客户端访问列表填回 <see cref="RelayConfiguration.Clients"/>，得到一份完整的配置视图；只读，不写回任何存储。</summary>
    public async Task<RelayConfiguration> Read()
    {
        var config = (await settings.Read()).Value;
        config.Clients = (await identities.ReadAccess()).ToList();
        return config;
    }
    /// <summary>命令行 init：按花生壳域名和端口生成私有 CA 连接配置；已有服务器证书时直接拒绝，避免覆盖正在使用的证书。管理员和个人令牌不在这里创建，而是之后经 serve 的本机网页设置。</summary>
    public async Task Initialize(string host, int publicPort, int listenPort)
    {
        if (await settings.HasCertificate()) throw new InvalidOperationException("已有证书，请使用现有配置启动服务。");
        await settings.ConfigureLegacyTls(host, publicPort, listenPort);
        Console.WriteLine("已保存私有 CA 连接配置。请运行 serve，通过本机网页设置管理员并逐人发放令牌。");
    }

    /// <summary>命令行 account：只在本机交互终端逐键隐藏读取上游令牌（拒绝重定向输入，避免令牌进入命令行历史或管道），加密保存为候选配置；不改动活动上游，须到本机网页填写模型、测试后再激活。</summary>
    public async Task SetAccount()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("请在本机交互终端输入令牌，不接受命令行参数或重定向输入。");
        Console.WriteLine("请粘贴上游访问令牌。输入隐藏，按回车保存：");
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) characters.RemoveAt(characters.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
        }
        var token = new string(characters.ToArray()).Trim(); characters.Clear();
        await settings.SaveCredential(token);
        Console.WriteLine("上游凭据已加密保存为候选配置。请在本机网页填写模型、测试后激活。");
    }

    /// <summary>命令行 clients：列出、新增、轮换、启用、停用或改名家庭成员；除 add 外按 <paramref name="id"/> 定位，缺省时用 <paramref name="name"/> 查找旧客户端。新增与轮换产生的个人令牌只在控制台显示这一次，权限变更立即生效。</summary>
    public async Task ManageClient(string action, string? name, string? id = null, string? displayName = null)
    {
        if (action == "list")
        {
            foreach (var user in await identities.List()) Console.WriteLine($"{user.Id}  {user.Name}  {user.DisplayName}  {(user.Enabled ? "启用" : "停用")}");
            return;
        }
        IssuedToken? issued = null;
        if (action == "add") issued = await identities.Create(displayName ?? name ?? throw new InvalidOperationException("请提供 --display-name 或 --name。"), name);
        else
        {
            // 主键是 clients list 第一列的数字；命令行收到的是文本，这里只做解析，不按文本关联。
            long? userId = id is null ? null : long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
                : throw new InvalidOperationException("--id 应是 clients list 第一列的数字主键。");
            userId ??= (await identities.List()).SingleOrDefault(user => user.Name == name)?.Id;
            if (userId is not { } user) throw new InvalidOperationException("请用 --id 指定使用者主键，或用 --name 查找旧客户端。");
            switch (action)
            {
                case "rotate": issued = await identities.Rotate(user); break;
                case "enable": await identities.SetEnabled(user, true); break;
                case "disable": await identities.SetEnabled(user, false); break;
                case "rename": await identities.Rename(user, displayName ?? throw new InvalidOperationException("请提供 --display-name。")); break;
                default: throw new InvalidOperationException("clients 支持 list / add / rotate / enable / disable / rename。");
            }
        }
        Console.WriteLine("已保存，新的权限检查立即生效。");
        if (issued is not null)
        {
            Console.WriteLine($"使用者：{issued.UserId}；个人令牌仅显示这一次：");
            Console.WriteLine(issued.Token);
        }
    }
}
