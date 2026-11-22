namespace Core.Upstream;

/// <summary>已保存上游此刻的检测阶段，由 <see cref="UpstreamCandidate.Phase"/> 从状态与开始时刻推出；页面文字、删除与重新检测都读它，不各自看原始状态。</summary>
public enum UpstreamCheckPhase
{
    /// <summary>不在检测：未测试、通过或未通过。</summary>
    Idle,

    /// <summary>正在检测，开始还不到 <see cref="UpstreamCandidate.TestWindowSeconds"/> 秒。</summary>
    Checking,

    /// <summary>停在检测中却已超过检测窗口：检测没有走到结论，读作「检测未完成」，可以重新检测或删除。</summary>
    Interrupted,
}
