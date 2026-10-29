namespace Core.Usage;

/// <summary>同一币种、同一来源的金额合计；不同币种和来源从不相加。</summary>
public sealed record MoneyTotal(string Currency, string Source, decimal Amount, long Requests, long Incomplete);
