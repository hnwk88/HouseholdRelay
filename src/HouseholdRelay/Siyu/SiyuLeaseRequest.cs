namespace HouseholdRelay.Siyu;

/// <summary>续租请求体：只有单调递增的 <see cref="LeaseSequence"/>，同序号重试幂等。</summary>
public sealed record SiyuLeaseRequest(long LeaseSequence);
