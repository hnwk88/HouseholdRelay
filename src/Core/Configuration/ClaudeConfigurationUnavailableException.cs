namespace Core.Configuration;

/// <summary>工程私有 Claude 身份配置未就绪的明确本地拒绝；固定读句不包含原始配置、设备值或凭据。</summary>
public sealed class ClaudeConfigurationUnavailableException(string message) : InvalidOperationException(message);
