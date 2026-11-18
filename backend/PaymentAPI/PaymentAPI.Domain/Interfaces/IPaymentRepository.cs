using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Entities;

namespace PaymentAPI.Interfaces
{
    public interface IPaymentRepository
    {
        Task<List<Transaction>> GetUserTransactionsAsync(int userId);
        Task<(List<Transaction> Items, int TotalCount)> GetTransactionsAsync(
            int? userId,
            string? type,
            string? status,
            string? search,
            int pageNumber,
            int pageSize);
        Task<Transaction?> GetTransactionByIdAsync(Guid id);
        Task<Transaction?> GetTransactionByCodeAsync(string transactionCode);
        Task<Transaction?> GetTransactionByNotePrefixAsync(string notePrefix);
        Task<UserPurchase?> GetUserPurchaseAsync(int userId, Guid chapterId);
        Task AddTransactionAsync(Transaction transaction);
        Task UpdateTransactionAsync(Transaction transaction);
        Task AddUserPurchaseAsync(UserPurchase userPurchase);
        Task BeginTransactionAsync();
        Task CommitTransactionAsync();
        Task RollbackTransactionAsync();
        Task SaveFailedTransactionAsync(Transaction transaction);

        // ---- Duplicate purchase guard (T45) ----

        /// <summary>
        /// Inside <see cref="BeginTransactionAsync"/>: takes an exclusive SQL Server application lock for this user and
        /// chapter, released by <see cref="CommitTransactionAsync"/> or <see cref="RollbackTransactionAsync"/>.
        /// False when it is not granted within <paramref name="timeoutMs"/>.
        /// </summary>
        Task<bool> AcquirePurchaseLockAsync(int userId, Guid chapterId, int timeoutMs);

        /// <summary>The Pending or Completed <c>Purchase</c> transaction of the user and chapter (Completed first), or null.</summary>
        Task<Transaction?> GetOpenPurchaseAsync(int userId, Guid chapterId);

        // ---- Purchase recovery job (T23) ----

        /// <summary>Ids of Pending <c>Purchase</c> transactions created before <paramref name="createdBefore"/>, oldest first.</summary>
        Task<List<Guid>> GetPendingPurchaseIdsAsync(DateTime createdBefore, int limit);

        /// <summary>
        /// Ids of Failed <c>Purchase</c> transactions whose note starts with <paramref name="notePrefix"/>, created after
        /// <paramref name="createdAfter"/> (re-checked for a debit that arrived after the job failed them).
        /// </summary>
        Task<List<Guid>> GetFailedPurchaseIdsByNotePrefixAsync(string notePrefix, DateTime createdAfter, int limit);

        /// <summary>
        /// Transaction ids of saga rows in one of <paramref name="states"/> whose last activity
        /// (<c>UpdatedAt ?? CreatedAt</c>) is before <paramref name="lastActivityBefore"/>.
        /// </summary>
        Task<List<Guid>> GetSagaTransactionIdsAsync(IReadOnlyCollection<string> states, DateTime lastActivityBefore, int limit);

        /// <summary>The saga row of a transaction, not tracked, or null.</summary>
        Task<PurchaseState?> GetPurchaseStateAsync(Guid transactionId);

        /// <summary>
        /// Inside <see cref="BeginTransactionAsync"/>: reads and locks (UPDLOCK, ROWLOCK) the saga row of a transaction,
        /// tracked, or null. Same lock the saga's EF repository takes, so a running saga step and the job never update
        /// the row at the same time.
        /// </summary>
        Task<PurchaseState?> LockPurchaseStateAsync(Guid transactionId);

        /// <summary>
        /// Inside <see cref="BeginTransactionAsync"/>: reads and locks (UPDLOCK, ROWLOCK) a transaction, tracked, or null.
        /// A later <see cref="GetTransactionByIdAsync"/> in the same scope returns this tracked row.
        /// </summary>
        Task<Transaction?> LockTransactionAsync(Guid id);

        Task UpdatePurchaseStateAsync(PurchaseState state);
        Task RemovePurchaseStateAsync(PurchaseState state);
    }
}
