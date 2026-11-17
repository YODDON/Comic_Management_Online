using SharedKernel.Contracts.Modules;

namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// Call log and failure switch for the two calls PaymentAPI makes into other modules (Chapter and Wallet).
/// The names are the gRPC full method names the tests used before T17 ("/chapter.ChapterGrpc/GetChapterInfo"),
/// kept so every test assertion on <see cref="Calls"/> and <see cref="Unavailable"/> stays as it was.
/// An "unavailable" method throws <see cref="ModuleCallException"/>, which is what the real gateways throw for
/// any failure (it replaces the <c>RpcException</c> a dead gRPC channel produced).
/// </summary>
public sealed class ModuleCallLog
{
    public const string GetChapterInfo = "/chapter.ChapterGrpc/GetChapterInfo";
    public const string UnlockChapter = "/chapter.ChapterGrpc/UnlockChapter";
    public const string AddCoin = "/wallet.WalletService/AddCoin";

    /// <summary>Full method names that currently fail with <see cref="ModuleCallException"/>.</summary>
    public HashSet<string> Unavailable { get; } = new();

    /// <summary>Every call made, in order, by full method name.</summary>
    public List<string> Calls { get; } = new();

    internal void Enter(string method)
    {
        lock (Calls) Calls.Add(method);

        if (Unavailable.Contains(method))
            throw new ModuleCallException("injected: service unavailable");
    }
}

/// <summary>The REAL <c>PaymentChapterGateway</c> (Chapter module) behind a call log and a failure switch.</summary>
public sealed class RecordingChapterGateway : IPaymentChapterGateway
{
    private readonly IPaymentChapterGateway _inner;
    private readonly ModuleCallLog _log;

    public RecordingChapterGateway(IPaymentChapterGateway inner, ModuleCallLog log)
    {
        _inner = inner;
        _log = log;
    }

    public Task<ChapterInfo> GetChapterInfoAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.GetChapterInfo);
        return _inner.GetChapterInfoAsync(chapterId, cancellationToken);
    }

    public Task<UnlockChapterResult> UnlockChapterAsync(int userId, Guid chapterId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.UnlockChapter);
        return _inner.UnlockChapterAsync(userId, chapterId, cancellationToken);
    }
}

/// <summary>The REAL <c>PaymentWalletGateway</c> (Wallet module) behind a call log and a failure switch.</summary>
public sealed class RecordingWalletGateway : IPaymentWalletGateway
{
    private readonly IPaymentWalletGateway _inner;
    private readonly ModuleCallLog _log;

    public RecordingWalletGateway(IPaymentWalletGateway inner, ModuleCallLog log)
    {
        _inner = inner;
        _log = log;
    }

    public Task<WalletCreditResult> AddCoinAsync(
        int userId, decimal amount, Guid referenceId, string description, string creditType,
        CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.AddCoin);
        return _inner.AddCoinAsync(userId, amount, referenceId, description, creditType, cancellationToken);
    }
}
