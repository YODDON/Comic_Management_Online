using System;

namespace SharedKernel.Contracts.Purchase
{
    // COMMAND: Sent from REST endpoint to initiate the saga
    public record SubmitPurchaseCommand(Guid CorrelationId, int UserId, Guid ComicId, Guid ChapterId, decimal Price, Guid TransactionId);

    // COMMAND: Saga -> WalletAPI
    // Parameterless constructors below: PurchaseStateMachine builds these messages with context.Init<T>(),
    // which needs a default constructor (MassTransit 8: "No default constructor available for message type").
    public record DebitWalletCommand(Guid CorrelationId, int UserId, decimal Amount, Guid TransactionId)
    {
        public DebitWalletCommand() : this(default, default, default, default) { }
    }

    // EVENT: WalletAPI -> Saga
    public record WalletDebitedEvent(Guid CorrelationId, decimal RemainingBalance);
    public record WalletDebitFailedEvent(Guid CorrelationId, string Reason);

    // COMMAND: Saga -> ChapterAPI
    public record UnlockChapterCommand(Guid CorrelationId, int UserId, Guid ChapterId)
    {
        public UnlockChapterCommand() : this(default, default, default) { }
    }

    // EVENT: ChapterAPI -> Saga
    public record ChapterUnlockedEvent(Guid CorrelationId);
    public record ChapterUnlockFailedEvent(Guid CorrelationId, string Reason);

    // COMMAND: Saga -> WalletAPI (Compensation)
    public record RefundWalletCommand(Guid CorrelationId, int UserId, decimal Amount, Guid TransactionId)
    {
        public RefundWalletCommand() : this(default, default, default, default) { }
    }

    // EVENT: WalletAPI -> Saga (Compensation result)
    public record WalletRefundedEvent(Guid CorrelationId);

    // EVENT: Saga -> REST endpoint (Response to IRequestClient)
    public record PurchaseCompletedEvent(Guid CorrelationId, decimal RemainingBalance, Guid TransactionId, Guid ComicId)
    {
        public PurchaseCompletedEvent() : this(default, default, default, default) { }
    }
    public record PurchaseFailedEvent(Guid CorrelationId, string Reason, Guid TransactionId)
    {
        public PurchaseFailedEvent() : this(default, string.Empty, default) { }
    }
}
