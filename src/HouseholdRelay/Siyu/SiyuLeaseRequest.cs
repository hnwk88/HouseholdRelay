namespace HouseholdRelay.Siyu;

/// <summary>
/// 续租请求体：单调递增的 <see cref="LeaseSequence"/>，同序号重试幂等；<see cref="ClientProtocolVersion"/> 可省（省了按版本 1），
/// 版本 2 客户端带上，会话找不到（中转重启后）时中转据此决定拒绝带不带打回。
/// </summary>
public sealed record SiyuLeaseRequest(long LeaseSequence, int ClientProtocolVersion = Core.Siyu.SiyuContract.LegacyProtocolVersion);
