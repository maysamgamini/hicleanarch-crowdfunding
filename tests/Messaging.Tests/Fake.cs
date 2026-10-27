using System.Reflection;

namespace Messaging.Tests;

/// <summary>An interface implemented by one function of the method and its arguments, for the few members a test needs.</summary>
public static class Fake
{
    public static T Of<T>(Func<MethodInfo, object?[], object?> call)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Call = call;
        return proxy;
    }

    public class Proxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Call { get; set; } = (m, _) => throw new NotSupportedException(m.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
