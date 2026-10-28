using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.Data.Sqlite;

/// <summary>仅显式调用时复用本项目预检 DPAPI 材料，通过管道交给真实浏览器验证，不输出凭据。</summary>
static class RealUpstreamValidation
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length is < 5 or > 6 || args.Length == 6 && args[5] != "--client-only")
            throw new ArgumentException("需要两份预检结果、发布 DLL 和证据目录；可选 --client-only 仅验证客户端文件信任。");
        object Read(string manifestPath, string host, string diagnostic)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var directory = Path.GetFullPath(manifest.RootElement.GetProperty("temporary_home").GetString()!);
            var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "HouseholdRelay-real-upstream-");
            if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("只复用本项目专用临时目录。");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, ".state", "relay.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT options_json,credential FROM upstream_candidates ORDER BY created_at DESC";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var options = JsonSerializer.Deserialize<UpstreamOptions>(rows.GetString(0), ConfigurationFiles.Json)!;
                if (new Uri(options.BaseUrl).Host != host) continue;
                var bytes = WindowsSecrets.Unprotect((byte[])rows[1]);
                try { return new { options = options with { Diagnostic = diagnostic }, credential = Encoding.UTF8.GetString(bytes) }; }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            throw new InvalidOperationException("预检目录缺少目标上游。");
        }
        var material = new { providers = new[] { Read(args[1], "aitoll.net", "claude-code"), Read(args[2], "api.sftoken.top", "messages") }, claude = ClientLauncher.FindClaude() };
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { Path.GetFullPath("HouseholdRelay.Tests/real-upstream-browser.cjs"), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]) }) start.ArgumentList.Add(argument);
        if (args.Length == 6) start.ArgumentList.Add(args[5]);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器验证。"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(material, ConfigurationFiles.Json)); child.StandardInput.Close();
        await child.WaitForExitAsync(); return child.ExitCode;
    }
}
