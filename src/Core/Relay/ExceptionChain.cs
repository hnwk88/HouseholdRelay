namespace Core.Relay;

/// <summary>沿内部异常链查找：建连、代理、超时这类失败常被一层层包起来，判断「到底是哪一种」都走这一处。</summary>
public static class ExceptionChain
{
    /// <summary>从 <paramref name="error"/> 自己开始沿 <see cref="Exception.InnerException"/> 往里找，返回第一个 <typeparamref name="T"/>；没有时为 null。</summary>
    public static T? Find<T>(Exception error) where T : Exception
    {
        for (Exception? inner = error; inner is not null; inner = inner.InnerException) if (inner is T match) return match;
        return null;
    }

    /// <summary>
    /// 异常类型链：从外到里的完整类型名，用「 &lt; 」连起来（最多 8 层），例如 <c>System.Net.Http.HttpRequestException &lt; System.Net.Sockets.SocketException</c>。
    /// 只有类型名，不含消息、堆栈或任何数据：日志里记异常一律只记它，消息里可能带令牌、凭据、路径或代理地址。
    /// </summary>
    public static string Types(Exception error)
    {
        var names = new List<string>();
        for (Exception? inner = error; inner is not null && names.Count < 8; inner = inner.InnerException) names.Add(inner.GetType().FullName ?? inner.GetType().Name);
        return string.Join(" < ", names);
    }
}
