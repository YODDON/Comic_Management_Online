using System;
using System.Threading.Tasks;
using PaymentAPI.Entities;
using PaymentAPI.Interfaces;
using SharedKernel.Enums;

namespace PaymentAPI.Application.Services
{
    /// <summary>
    /// The Payment side effects of a finished chapter purchase, shared by <c>PurchaseCompletedConsumer</c>,
    /// <c>PurchaseFailedConsumer</c> and the purchase recovery job (T23). Moved here unchanged from the two consumers.
    /// The caller owns the database transaction (<see cref="IPaymentRepository.BeginTransactionAsync"/> /
    /// <see cref="IPaymentRepository.CommitTransactionAsync"/>).
    /// </summary>
    public static class PurchaseOutcomes
    {
        /// <summary>
        /// Marks the transaction Completed and adds the Payment <see cref="UserPurchase"/> if missing.
        /// Does nothing when the transaction does not exist or is already Completed.
        /// </summary>
        public static async Task RecordCompletedAsync(IPaymentRepository repository, Guid transactionId, Guid comicId)
        {
            var transaction = await repository.GetTransactionByIdAsync(transactionId);
            if (transaction == null || transaction.Status == TransactionStatus.Completed)
            {
                return;
            }

            transaction.Status = TransactionStatus.Completed;
            await repository.UpdateTransactionAsync(transaction);

            // Create UserPurchase
            var existingPurchase = await repository.GetUserPurchaseAsync(transaction.UserId, transaction.ChapterId.Value);
            if (existingPurchase == null)
            {
                await repository.AddUserPurchaseAsync(new UserPurchase
                {
                    UserId = transaction.UserId,
                    ComicId = comicId, // We need ComicId! Wait, SubmitPurchaseCommand needs ComicId!
                    ChapterId = transaction.ChapterId.Value,
                    Price = transaction.Amount,
                    PurchasedAt = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Marks the transaction Failed with <paramref name="reason"/> as its note.
        /// Does nothing when the transaction does not exist or is not Pending.
        /// </summary>
        public static async Task RecordFailedAsync(IPaymentRepository repository, Guid transactionId, string reason)
        {
            var transaction = await repository.GetTransactionByIdAsync(transactionId);
            if (transaction == null || transaction.Status != TransactionStatus.Pending)
            {
                return;
            }

            transaction.Status = TransactionStatus.Failed;
            transaction.Note = reason;
            await repository.UpdateTransactionAsync(transaction);
        }
    }
}
