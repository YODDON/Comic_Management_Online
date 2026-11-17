using System;

namespace SharedKernel.Contracts.Purchase
{
    /// <summary>
    /// Wallet ledger reference ids of a chapter purchase (<c>CurrencyEntry.ReferenceId</c>, unique in WalletDB).
    /// The debit uses the Payment transaction id itself (<c>DebitWalletConsumer</c>); the refund uses
    /// <see cref="RefundOf"/>. A second debit or refund with the same reference id is not applied again.
    /// </summary>
    public static class PurchaseReferenceIds
    {
        /// <summary>
        /// Refund reference id of a purchase: the transaction id with its last byte flipped (XOR 0xFF).
        /// Same value as <c>RefundWalletConsumer</c> computes, so a refund made by the saga and one made by
        /// the purchase recovery job can never both be applied.
        /// </summary>
        public static Guid RefundOf(Guid transactionId)
        {
            var bytes = transactionId.ToByteArray();
            bytes[15] ^= 0xFF;
            return new Guid(bytes);
        }
    }
}
