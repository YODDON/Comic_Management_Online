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

    /// <summary>
    /// Reads the wallet ledger entry with this reference id, or null when there is none. Read only. Used by the
    /// purchase recovery job (T23) to see whether a purchase was debited (<c>ReferenceId == TransactionId</c>) or
    /// refunded (<c>PurchaseReferenceIds.RefundOf</c>). Any failure is a <see cref="ModuleCallException"/>.
    /// </summary>
    Task<WalletLedgerEntry?> GetLedgerEntryAsync(Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds a chapter purchase: credits <paramref name="amount"/> as <c>TransactionType.Refund</c> with the
    /// reference id <c>PurchaseReferenceIds.RefundOf(transactionId)</c> and the same description as
    /// <c>RefundWalletConsumer</c>. Idempotent: a second call does not credit again and still answers
    /// <see cref="WalletCreditResult.Success"/> true. Any failure is a <see cref="ModuleCallException"/>.
    /// </summary>
    Task<WalletCreditResult> RefundPurchaseAsync(
        int userId,
        decimal amount,
        Guid transactionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Fields of the gRPC <c>AddCoinResponse</c>.</summary>
public sealed record WalletCreditResult(bool Success, string Message);

/// <summary>
/// One wallet ledger entry (<c>CurrencyEntry</c>). <see cref="Amount"/> is signed: a debit is negative.
/// <see cref="Type"/> is the <c>SharedKernel.Enums.TransactionType</c> name, for example "Purchase" or "Refund".
/// </summary>
public sealed record WalletLedgerEntry(int UserId, decimal Amount, string Type);
