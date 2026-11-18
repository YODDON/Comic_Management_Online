using SharedKernel.Enums;

namespace WalletAPI.DTOs;

public record WalletCreditResultDto(bool AlreadyProcessed, decimal Balance);

public record WalletDebitResultDto(
    bool Success,
    bool AlreadyProcessed,
    bool InsufficientBalance,
    decimal Balance);

/// <summary>One ledger entry read by reference id (T23 purchase recovery). Amount is signed: a debit is negative.</summary>
public record WalletLedgerEntryDto(int UserId, decimal Amount, TransactionType Type);
