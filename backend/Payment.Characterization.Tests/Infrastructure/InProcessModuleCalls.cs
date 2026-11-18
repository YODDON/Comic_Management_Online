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

    // Added by T23 (purchase recovery job); never gRPC methods, named the same way for the log.
    public const string IsChapterUnlocked = "/chapter.ChapterGrpc/IsChapterUnlocked";
    public const string GetLedgerEntry = "/wallet.WalletService/GetLedgerEntry";
    public const string RefundPurchase = "/wallet.WalletService/RefundPurchase";

    /// <summary>Full method names that currently fail with <see cref="ModuleCallException"/>.</summary>
    public HashSet<string> Unavailable { get; } = new();

    /// <summary>
    /// T23: runs after every <c>IsChapterUnlocked</c> answer (user id, chapter id), before the caller continues. Lets a
    /// test change the world between the recovery job's read and its locked re-check.
    /// </summary>
    public Func<int, Guid, Task>? AfterUnlockCheck { get; set; }

    /// <summary>
    /// T45: runs after every <c>GetChapterInfo</c> answer, before the caller continues. In the purchase handler this is
    /// after the first "already purchased" guard and before the locked check, so a test can hold two requests there.
    /// </summary>
    public Func<Task>? AfterChapterInfo { get; set; }

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

    public async Task<ChapterInfo> GetChapterInfoAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.GetChapterInfo);
        var info = await _inner.GetChapterInfoAsync(chapterId, cancellationToken);
        if (_log.AfterChapterInfo is { } hook) await hook();
        return info;
    }

    public Task<UnlockChapterResult> UnlockChapterAsync(int userId, Guid chapterId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.UnlockChapter);
        return _inner.UnlockChapterAsync(userId, chapterId, cancellationToken);
    }

    public async Task<bool> IsChapterUnlockedAsync(int userId, Guid chapterId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.IsChapterUnlocked);
        var unlocked = await _inner.IsChapterUnlockedAsync(userId, chapterId, cancellationToken);
        if (_log.AfterUnlockCheck is { } hook) await hook(userId, chapterId);
        return unlocked;
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

    public Task<WalletLedgerEntry?> GetLedgerEntryAsync(Guid referenceId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.GetLedgerEntry);
        return _inner.GetLedgerEntryAsync(referenceId, cancellationToken);
    }

    public Task<WalletCreditResult> RefundPurchaseAsync(
        int userId, decimal amount, Guid transactionId, CancellationToken cancellationToken = default)
    {
        _log.Enter(ModuleCallLog.RefundPurchase);
        return _inner.RefundPurchaseAsync(userId, amount, transactionId, cancellationToken);
    }
}
