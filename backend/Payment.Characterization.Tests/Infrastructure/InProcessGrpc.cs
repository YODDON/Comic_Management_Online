using Google.Protobuf;
using Grpc.Core;

namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// A gRPC CallInvoker that skips the network: each call is serialized, handed to a route that runs the REAL
/// server implementation (ChapterGrpcService / WalletGrpcService) in the other module's DI container, and the
/// answer is deserialized again. Failure injection (an unreachable service) throws the same RpcException a
/// dead channel would.
/// </summary>
public sealed class InProcessCallInvoker : CallInvoker
{
    private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _routes = new();

    /// <summary>Full method names ("/chapter.ChapterGrpc/GetChapterInfo") that currently fail with Unavailable.</summary>
    public HashSet<string> Unavailable { get; } = new();

    /// <summary>Every call made, in order, by full method name.</summary>
    public List<string> Calls { get; } = new();

    public void Route(string fullMethodName, Func<byte[], Task<byte[]>> handler) => _routes[fullMethodName] = handler;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var response = InvokeAsync(method, request);
        return new AsyncUnaryCall<TResponse>(
            response,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private async Task<TResponse> InvokeAsync<TRequest, TResponse>(Method<TRequest, TResponse> method, TRequest request)
        where TRequest : class where TResponse : class
    {
        lock (Calls) Calls.Add(method.FullName);

        if (Unavailable.Contains(method.FullName))
            throw new RpcException(new Status(StatusCode.Unavailable, "injected: service unavailable"));

        var bytes = ((IMessage)request).ToByteArray();
        var answer = await _routes[method.FullName](bytes);
        // Generated marshallers only implement the "contextual" deserializer, so parse through the message's own parser.
        var parser = (MessageParser)typeof(TResponse).GetProperty("Parser")!.GetValue(null)!;
        return (TResponse)parser.ParseFrom(answer);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException();

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException();

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException();
}
