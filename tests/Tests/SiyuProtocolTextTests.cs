using System.Text.RegularExpressions;
using Core.Siyu;
using Infrastructure.ExternalSources.Siyu;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 思域协议文本 <c>docs/siyu/PROTOCOL.md</c> 是两边唯一依据：这里读它标了「契约表」的表格，与代码逐项对照。
/// 改了代码没改文本、或改了文本没改代码，都在这里失败。
/// </summary>
public static class SiyuProtocolTextTests
{
    /// <summary>拒绝原因码与格式码可能出现的源文件：思域四层目录，加上官方凭据读取。</summary>
    private static readonly string[] _sourceFolders =
    [
        "src/Core/Siyu", "src/Application/Services/Siyu", "src/Infrastructure/ExternalSources/Siyu", "src/HouseholdRelay/Siyu",
    ];

    /// <summary>思域之外也抛思域拒绝的文件。</summary>
    private const string OfficialLogin = "src/Infrastructure/ExternalSources/ClaudeCode/OfficialAccountLogin.cs";

    /// <summary>数值表与 <see cref="SiyuContract"/>、<see cref="SiyuProxyLimits.Default"/> 逐项相同，一项不多一项不少。</summary>
    public static Task ValuesMatchContract()
    {
        var limits = SiyuProxyLimits.Default(1);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProtocolVersion"] = SiyuContract.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RequestStrategy"] = SiyuContract.RequestStrategy,
            ["Transport"] = SiyuContract.Transport,
            ["SupportedClaudeVersions"] = string.Join("、", SiyuContract.SupportedClaudeVersions),
            ["SupportedEntrypoints"] = string.Join("、", SiyuContract.SupportedEntrypoints),
            ["LeaseDurationSeconds"] = Seconds(SiyuContract.LeaseDuration),
            ["LeaseRenewAfterSeconds"] = Seconds(SiyuContract.LeaseRenewAfter),
            ["MaxSessionsPerUser"] = SiyuContract.MaxSessionsPerUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MaxTunnelsPerSession"] = SiyuContract.MaxTunnelsPerSession.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CreateReplayRetentionSeconds"] = Seconds(SiyuContract.CreateReplayRetention),
            ["ClosedRetentionSeconds"] = Seconds(SiyuContract.ClosedRetention),
            ["McpDiscoveryLifetimeSeconds"] = Seconds(SiyuContract.McpDiscoveryLifetime),
            ["McpDiscoveryTimeoutSeconds"] = Seconds(SiyuContract.McpDiscoveryTimeout),
            ["SweepIntervalSeconds"] = Seconds(SiyuContract.SweepInterval),
            ["ProxyHeadTimeoutSeconds"] = Seconds(limits.HeadTimeout),
            ["ProxyHandshakeTimeoutSeconds"] = Seconds(limits.HandshakeTimeout),
            ["ProxyIdleTimeoutSeconds"] = Seconds(limits.IdleTimeout),
        };
        var rows = Table("数值");
        var actual = rows.ToDictionary(row => row[0], row => row[1], StringComparer.Ordinal);
        Check(actual.Count == rows.Count, "数值表里有重复的名字");
        foreach (var (name, value) in expected)
            Check(actual.TryGetValue(name, out var written) && written == value, $"协议文本的 {name} 应为 {value}，实际是 {(written ?? "（没写）")}");
        Check(actual.Keys.All(expected.ContainsKey), "协议文本的数值表多了代码里没有的项：" + string.Join("、", actual.Keys.Where(key => !expected.ContainsKey(key))));
        // 文本头的线上协议版本与数值表一致。
        Check(Text().Contains("线上协议版本：" + SiyuContract.ProtocolVersion, StringComparison.Ordinal), "文本头的线上协议版本应与代码一致");
        return Task.CompletedTask;
    }

    /// <summary>主机表就是两个官方目标；路由表每一行代码都放行且用途相同，用途覆盖全部 <see cref="SiyuPurpose"/>。</summary>
    public static Task HostsAndRoutesMatchCode()
    {
        var hosts = Table("主机");
        Check(hosts.Count == 2 && hosts.Select(row => row[0]).ToHashSet().SetEquals([OfficialTargets.ApiHost, OfficialTargets.McpProxyHost]),
            "主机表应恰好是两个官方主机");
        Check(hosts.All(row => row[1] == OfficialTargets.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)), "主机表的端口应都是 443");
        var purposes = new HashSet<SiyuPurpose>();
        foreach (var row in Table("路由"))
        {
            var host = row[0] == OfficialTargets.ApiHost ? OfficialHost.Api : row[0] == OfficialTargets.McpProxyHost ? OfficialHost.McpProxy
                : throw new Exception("路由表里有不认识的主机：" + row[0]);
            var path = row[2].Replace("{连接器编号}", "server_abc-123", StringComparison.Ordinal);
            foreach (var method in row[1].Split('、'))
            {
                Check(SiyuRoutes.TryMatch(host, method, path, out var route), $"代码应放行路由表里的 {method} {row[0]}{path}");
                Check(route.Purpose.ToString() == row[3], $"{method} {path} 的用途应是 {route.Purpose}，文本写的是 {row[3]}");
                purposes.Add(route.Purpose);
            }
        }
        Check(purposes.SetEquals(Enum.GetValues<SiyuPurpose>()), "路由表应覆盖全部用途");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 拒绝表与格式表合起来，与代码里的原因码一一对应：文本里的每个码在代码里有字面量（或是会话结束原因）；
    /// 代码里每个按字面量抛出的码都在文本里，抛出时的状态码也在那一行的状态码里；会话结束原因的状态码含 409（会话已结束时）。
    /// </summary>
    public static Task RefusalCodesMatchCode()
    {
        var documented = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var row in Table("拒绝").Concat(Table("格式")))
        {
            var statuses = row[1].Split('、').Select(int.Parse).ToHashSet();
            if (documented.TryGetValue(row[0], out var existing)) existing.UnionWith(statuses); else documented[row[0]] = statuses;
        }
        var source = Sources();
        var closeCodes = Enum.GetValues<SiyuCloseReason>().ToDictionary(reason => reason, SiyuCloseReasons.Code);
        foreach (var code in documented.Keys)
            Check(closeCodes.ContainsValue(code) || source.Contains('"' + code + '"', StringComparison.Ordinal), $"协议文本里的原因码 {code} 在代码里找不到");
        var thrown = new List<(string Code, int Status)>();
        foreach (Match match in Regex.Matches(source, """(?:SiyuRefusal|Http1Rejection|\bnew)\((\d{3}), "([a-z]+(?:-[a-z]+)*)"|WriteAsync\(\w+, (\d{3}), "([a-z]+(?:-[a-z]+)*)"|\bRefuse\("([a-z]+(?:-[a-z]+)*)"|\(SiyuCloseReason\.(\w+), (\d{3})\)"""))
        {
            if (match.Groups[2].Success) thrown.Add((match.Groups[2].Value, int.Parse(match.Groups[1].Value)));
            else if (match.Groups[4].Success) thrown.Add((match.Groups[4].Value, int.Parse(match.Groups[3].Value)));
            else if (match.Groups[5].Success) thrown.Add((match.Groups[5].Value, 503));
            else thrown.Add((closeCodes[Enum.Parse<SiyuCloseReason>(match.Groups[6].Value)], int.Parse(match.Groups[7].Value)));
        }
        Check(thrown.Count >= 60, $"代码里按字面量抛出的拒绝只找到 {thrown.Count} 处，扫描规则可能失效了");
        foreach (var (code, status) in thrown)
            Check(documented.TryGetValue(code, out var statuses) && statuses.Contains(status), $"代码里以状态码 {status} 抛出的原因码 {code} 没写进协议文本");
        foreach (var code in closeCodes.Values)
            Check(documented.TryGetValue(code, out var statuses) && statuses.Contains(409), $"会话结束原因 {code} 在协议文本里应含 409");
        return Task.CompletedTask;
    }

    /// <summary>拒绝表里写了完整读句的行，与代码里的读句逐字相同（带「{」占位或说明的行不比）。</summary>
    public static Task RefusalSentencesMatchCode()
    {
        var source = Sources();
        var compared = 0;
        foreach (var row in Table("拒绝"))
        {
            foreach (var sentence in row[2].Split('／'))
            {
                if (sentence.Contains('{') || !sentence.EndsWith('。') || sentence.Contains("读句", StringComparison.Ordinal) || sentence.Contains("说明", StringComparison.Ordinal)) continue;
                Check(source.Contains('"' + sentence + '"', StringComparison.Ordinal), $"原因码 {row[0]} 的读句在代码里找不到：{sentence}");
                compared++;
            }
        }
        Check(compared >= 30, $"只比对了 {compared} 句读句，表格可能被改坏了");
        return Task.CompletedTask;
    }

    /// <summary>秒数写法：整数秒。</summary>
    private static string Seconds(TimeSpan value) => ((long)value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>协议文本全文。</summary>
    private static string Text() => File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "siyu", "PROTOCOL.md"));

    /// <summary>
    /// 读「<c>&lt;!-- 契约表:名 --&gt;</c>」下面紧跟的表格：跳过表头与分隔行，每行按竖线切成去掉首尾空白的单元格。找不到或空表都算文本坏了。
    /// </summary>
    private static List<string[]> Table(string name)
    {
        var lines = Text().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var marker = Array.IndexOf(lines, "<!-- 契约表:" + name + " -->");
        Check(marker >= 0, "协议文本里找不到契约表：" + name);
        var rows = lines.Skip(marker + 1).TakeWhile(line => line.StartsWith('|')).Skip(2)
            .Select(line => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim().Trim('`')).ToArray()).ToList();
        Check(rows.Count > 0, "契约表是空的：" + name);
        return rows;
    }

    /// <summary>可能抛出思域拒绝的全部源文件合在一起的文本。</summary>
    private static string Sources()
    {
        var root = RepositoryRoot();
        var files = _sourceFolders.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs")).Append(Path.Combine(root, OfficialLogin));
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    /// <summary>仓库根目录，与读源码的分层用例同一处。</summary>
    private static string RepositoryRoot() => LayeringTests.RepositoryRoot();
}
