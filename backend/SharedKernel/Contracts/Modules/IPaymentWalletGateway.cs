namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction PaymentAPI to WalletAPI. Replaces the gRPC call <c>WalletService.AddCoin</c> (T15, T16).
/// Declaration only: nothing implements or uses it before T17. Sensitive area (deposits, wallet credit):
/// T17 needs an approved plan and the T37 tests. <c>WalletService.DebitCoin</c> has no caller (debits go through
/// <c>DebitWalletCommand</c>), so it has no method here.
/// </summary>
public interface IPaymentWalletGateway
{
    /// <summary>
    /// Credits coins to a wallet. <paramref name="referenceId"/> makes the call idempotent: a second call with
    /// the same reference does not credit again and still answers <see cref="WalletCreditResult.Success"/> true.
    /// Current gRPC server rule: a user id of 0 or less, an amount of 0 or less, or a reference id that is not a
    /// valid Guid is rejected with <c>InvalidArgument</c> (<see cref="Guid.Empty"/> is accepted today). Callers today treat a thrown error or <c>Success = false</c> as
    /// "not credited" (the deposit stays Pending and SePay retries).
    /// </summary>
    /// <param name="amount">Coins. The gRPC message carries a <c>double</c>; the server converts back to <c>decimal</c>.</param>
    /// <param name="creditType">Name of a <c>SharedKernel.Enums.TransactionType</c>, for example "ManualTopUp".</param>
    Task<WalletCreditResult> AddCoinAsync(
        int userId,
        decimal amount,
        Guid referenceId,
        string description,
        string creditType,
        CancellationToken cancellationToken = default);
}

/// <summary>Fields of the gRPC <c>AddCoinResponse</c>.</summary>
public sealed record WalletCreditResult(bool Success, string Message);
