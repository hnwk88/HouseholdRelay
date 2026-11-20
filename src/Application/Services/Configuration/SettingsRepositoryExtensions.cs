using Core.Configuration;
using Repositories;

namespace Application.Services.Configuration;

/// <summary>设置那一行的唯一读法：按固定主键取出，缺了就报恢复备份。配置、证书、站点身份三处都经它，不各写一份。</summary>
public static class SettingsRepositoryExtensions
{
    /// <summary>取出设置那一行（跟踪中，可直接改了提交）；缺失时抛出提示恢复配套备份。</summary>
    /// <param name="settings">设置仓储。</param>
    public static async Task<RelaySettings> LoadSettings(this IRepository<RelaySettings> settings) =>
        await settings.GetByIdAsync(RelaySettings.SingletonId) ?? throw new InvalidOperationException("实例配置缺失，请恢复配套备份。");
}
