using System.Reflection;

namespace Tests;

/// <summary>
/// 按方法决定行为的接口假实现：每次调用都交给 <see cref="Handler"/>，它返回什么就是什么（属性读取的方法名是 get_属性名）。
/// 用来在真实实现外面包一层、只让某一个方法出故障，或者给后台服务一个只回答几样东西的依赖。
/// </summary>
public class FakeProxy<T> : DispatchProxy where T : class
{
    /// <summary>收到的每次调用：方法与参数，返回值交给调用方。</summary>
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;

    /// <summary>建一个交给 <paramref name="handler"/> 回答的假实现。</summary>
    public static T Create(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create<T, FakeProxy<T>>();
        ((FakeProxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>在真实实现 <paramref name="inner"/> 外面包一层：<paramref name="fault"/> 对某次调用返回异常时抛出它，否则原样转给真实实现（真实实现抛出的异常原样冒出）。</summary>
    public static T Wrap(T inner, Func<MethodInfo, Exception?> fault) => Create((method, args) =>
    {
        if (fault(method) is { } error) throw error;
        try { return method.Invoke(inner, args); }
        // 反射调用把真实实现的异常包了一层，拆开再抛，调用方看到的与直接调用相同。
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(wrapped.InnerException); throw; }
    });

    /// <summary>把调用交给 <see cref="Handler"/>。</summary>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
