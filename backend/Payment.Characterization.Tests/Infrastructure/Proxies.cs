using System.Reflection;

namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// Stand-in for collaborators that are NOT under test (Cloudinary, comic validation, mission notifier):
/// every method returns a completed Task / default value.
/// </summary>
public class NoOpProxy : DispatchProxy
{
    public static T Create<T>() where T : class => DispatchProxy.Create<T, NoOpProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>
/// Forwards every call to the real object, but lets a test make selected calls throw. Used to inject
/// "the database call failed" into a consumer without touching production code.
/// </summary>
public class FaultInjectingProxy : DispatchProxy
{
    private object _target = null!;
    private Func<MethodInfo, object?[], Exception?> _fault = null!;

    public static T Wrap<T>(T target, Func<MethodInfo, object?[], Exception?> fault) where T : class
    {
        var proxy = DispatchProxy.Create<T, FaultInjectingProxy>();
        var self = (FaultInjectingProxy)(object)proxy;
        self._target = target;
        self._fault = fault;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var failure = _fault(targetMethod!, args ?? Array.Empty<object?>());
        if (failure != null) throw failure;
        try
        {
            return targetMethod!.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
