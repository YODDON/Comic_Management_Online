namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Thrown by an in-process module implementation (see the interfaces in this folder) when "the other module
/// could not do it": an invalid argument, or any other failure, which is then the <see cref="Exception.InnerException"/>.
/// It replaces the <c>RpcException</c> that the gRPC server produced, so a caller's existing decision point
/// (a <c>catch</c> that falls back or answers 503) keeps the same behaviour. Cancellation is not wrapped.
/// </summary>
public sealed class ModuleCallException : Exception
{
    public ModuleCallException(string message) : base(message)
    {
    }

    public ModuleCallException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
