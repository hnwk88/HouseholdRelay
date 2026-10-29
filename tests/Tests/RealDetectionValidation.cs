using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tests;

/// <summary>仅显式调用时，从现有实例库只读取出两家真实上游的地址与密钥，经标准输入交给浏览器脚本，按「只填名称、地址、密钥」重新接入；不输出凭据，也不改动那个库。</summary>
static class RealDetectionValidation
{
    /// <summary>
    /// 命令行首参为 --real-detection-browser 时运行：参数为现有实例的 relay.db（或经用户同意的 MyCoAI account-vault.json）、发布 DLL 和证据目录。
    /// 按 <see cref="Providers"/> 取出两家的名称、地址和密钥，连同本机 claude.exe 路径通过标准输入交给 real-detection-browser.cjs；返回脚本的退出码。
    /// </summary>
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("需要现有实例的 relay.db、发布 DLL 和证据目录。");
        var material = new { providers = Providers(args[1]), claude = TestProcess.ClientLauncher.FindClaude() };
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { Path.GetFullPath("tests/Tests/real-detection-browser.cjs"), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器验证。"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(material, ConfigurationFiles.Json)); child.StandardInput.Close();
        await child.WaitForExitAsync(); return child.ExitCode;
    }

    /// <summary>
    /// 两份真实验证脚本共用的材料：从 <paramref name="source"/>（现有实例的 relay.db，或经用户同意的 MyCoAI account-vault.json）只读取出 aitoll 与 sftoken 的名称、地址和密钥。
    /// 实例库以 SQLite 只读模式打开，绝不用本程序的迁移打开，免得把旧程序的库升级。
    /// </summary>
    public static object[] Providers(string source)
    {
        object Read(string host, string name)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(source), Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT options_json,credential FROM upstream WHERE credential IS NOT NULL UNION ALL SELECT options_json,credential FROM (SELECT * FROM upstream_candidates ORDER BY created_at DESC)";
            using var rows = command.ExecuteReader(); var seen = new List<string>();
            while (rows.Read())
            {
                using var options = JsonDocument.Parse(rows.GetString(0));
                var baseUrl = options.RootElement.GetProperty("baseUrl").GetString()!;
                seen.Add(new Uri(baseUrl).Host);
                if (new Uri(baseUrl).Host != host) continue;
                var bytes = WindowsSecrets.Unprotect((byte[])rows[1]);
                try { return new { name, baseUrl, credential = Encoding.UTF8.GetString(bytes) }; }
                // 解密后的凭据字节转成字符串后立即清零，即使构造结果时出错也不让明文留在内存中。
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            throw new InvalidOperationException("实例库里没有 " + host + " 的上游；库里已有的主机：" + string.Join("、", seen.Distinct()) + "。");
        }
        // 用户同意的另一来源：MyCoAI 账户库（account-vault.json）。按条目名称只读解密，地址用订单写定的两个，并核对库里记的主机一致。
        object Vault(string entryName, string baseUrl, string name)
        {
            using var vault = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(source)));
            foreach (var entry in vault.RootElement.EnumerateObject().First(item => item.Name.Equals("accounts", StringComparison.OrdinalIgnoreCase)).Value.EnumerateArray())
            {
                if (Text(entry, "name") != entryName) continue;
                var bytes = WindowsSecrets.Unprotect(Convert.FromBase64String(Text(entry, "encryptedPayload")!), Encoding.UTF8.GetBytes("MyCoAI.AccountVault.v1"));
                try
                {
                    using var payload = JsonDocument.Parse(bytes);
                    var recorded = Text(payload.RootElement, "baseUrl") ?? Text(entry, "baseUrl");
                    if (recorded is null || new Uri(recorded).Host != new Uri(baseUrl).Host) throw new InvalidOperationException(entryName + " 在 MyCoAI 里记的地址主机与订单不一致。");
                    return new { name, baseUrl, credential = Text(payload.RootElement, "apiToken") ?? throw new InvalidOperationException(entryName + " 没有 API 令牌。") };
                }
                // 解密出的负载字节读完立即清零。
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            throw new InvalidOperationException("MyCoAI 账户库里没有「" + entryName + "」。");
        }
        return source.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? [Vault("AIToll中转", "https://aitoll.net/v1/cc", "aitoll"), Vault("顺风中转", "https://api.sftoken.top", "sftoken")]
            : [Read("aitoll.net", "aitoll"), Read("api.sftoken.top", "sftoken")];
    }

    /// <summary>按名称不区分大小写读 JSON 对象的字符串属性；缺失或不是字符串时为 null。</summary>
    private static string? Text(JsonElement element, string name) =>
        element.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } found ? found.Value.GetString() : null;
}
