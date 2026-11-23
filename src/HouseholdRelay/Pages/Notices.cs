using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace HouseholdRelay.Pages;

/// <summary>
/// 管理页回发结果的读法：操作结果提示经 <see cref="ResultKey"/> 交给布局写进标题；字段错误按键取出，页面据此给输入框挂 <c>aria-invalid</c> 与 <c>aria-describedby</c>。
/// </summary>
public static class Notices
{
    /// <summary>本次请求里操作结果提示的存放键（<c>HttpContext.Items</c>）；<c>_Notice</c> 分部视图写，布局读。</summary>
    public const string ResultKey = "household-relay-result";

    /// <summary>某个字段的第一条错误；没有错误时为 null。</summary>
    /// <param name="state">页面的模型状态。</param>
    /// <param name="key">字段键，与 <c>AddModelError</c> 时用的同一个。</param>
    public static string? FieldError(this ModelStateDictionary state, string key) =>
        state.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : null;

    /// <summary>页面标题前要加的结果：有错误时读「没有完成：第一条错误」，否则读操作结果提示；都没有时为 null。</summary>
    /// <param name="state">页面的模型状态。</param>
    /// <param name="result">本次请求的操作结果提示。</param>
    public static string? TitleResult(ModelStateDictionary state, object? result) =>
        state.ErrorCount > 0 ? "没有完成：" + state.Values.SelectMany(entry => entry.Errors).First().ErrorMessage : result as string;
}
