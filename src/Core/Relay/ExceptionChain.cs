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
}
