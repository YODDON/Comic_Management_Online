using SharedKernel.Enums;
using WalletAPI.DTOs;

namespace WalletAPI.Interfaces;

public interface IWalletRepository
{
    Task<WalletCreditResultDto> CreditAsync(
        int userId, decimal amount, Guid referenceId, TransactionType type, string description);
    Task<WalletDebitResultDto> DebitAsync(
        int userId, decimal amount, Guid referenceId, string description);
    /// <summary>The ledger entry with this reference id, or null. Read only (T23 purchase recovery).</summary>
    Task<WalletLedgerEntryDto?> GetEntryByReferenceIdAsync(Guid referenceId);
    Task SaveChangesAsync();
}
