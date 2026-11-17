using System.Diagnostics;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.DTOs;
using PaymentAPI.Interfaces;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// Chapter purchase through the PurchaseStateMachine saga, after T39 fixed the contracts (parameterless constructors
/// for context.Init&lt;T&gt;), SaveFailedTransactionAsync (updates instead of re-inserting) and the refund reference id.
/// Expected values were observed by running the fixed code.
///
/// KNOWN LIMITATION (KnownLimitation_*, expected to flip in T41): the saga answers with RespondAsync while handling the
/// wallet / chapter events, which do not carry the original request id, and PurchaseState does not store it. So the
/// response never reaches the request client in PurchaseChapterCommandHandler: every purchase ends with the default
/// 30 s request timeout and HTTP 202 "being processed in the background" (success = false), although the database
/// outcome is already final.
/// </summary>
public abstract class PurchaseSagaScenarios
{
    protected abstract Task<TestWorld> WorldAsync();

    private const string TimeoutMessage = "Giao dịch đang được xử lý ngầm. Vui lòng kiểm tra lại sau.";

    private static async Task PurchaseAnswers202AfterTheTimeoutAsync(TestWorld world, int user, Guid chapter)
    {
        using var scope = world.PaymentScope();
        var controller = Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString());

        var watch = Stopwatch.StartNew();
        var result = await controller.PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = chapter });
        watch.Stop();

        Assert.Equal(202, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<ChapterPurchaseResultDto>>(((ObjectResult)result).Value);
        Assert.False(body.Success);
        Assert.Equal(TimeoutMessage, body.Message);
        Assert.Null(body.Data);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(29), $"Answered after {watch.Elapsed}, expected the 30 s request timeout.");
    }

    private static Guid RefundReference(Guid transactionId)
    {
        var bytes = transactionId.ToByteArray();
        bytes[15] ^= 0xFF;
        return new Guid(bytes);
    }

    private static async Task<PaymentAPI.Entities.Transaction> FinalTransactionAsync(TestWorld world, int user, TransactionStatus status)
    {
        Assert.True(await TestWorld.EventuallyAsync(async () => (await world.TransactionsAsync(user)).All(t => t.Status == status)),
            $"The Purchase transaction never reached {status}. Statuses: "
            + string.Join(", ", (await world.TransactionsAsync(user)).Select(t => t.Status))
            + ". Messages: " + string.Join(", ", world.Messages.Snapshot())
            + ". States: " + string.Join(", ", world.States.All()));
        var tx = Assert.Single(await world.TransactionsAsync(user)); // never inserted twice
        Assert.Equal(TransactionType.Purchase, tx.Type);
        Assert.Equal(120m, tx.Amount);
        Assert.Equal("Dâu", tx.CurrencyType);
        return tx;
    }

    private static async Task AssertSagaAsync(TestWorld world, int user, string? state, string[] transitions, string[] messages)
    {
        Assert.True(await TestWorld.EventuallyAsync(() => Task.FromResult(world.Messages.Snapshot().Count >= messages.Length)),
            "Messages: " + string.Join(", ", world.Messages.Snapshot()));
        await Task.Delay(500); // let anything that would follow arrive
        Assert.Equal(messages, world.Messages.Snapshot());
        Assert.Equal(transitions, world.States.All());
        var sagas = await world.SagasForAsync(user);
        if (state == null) Assert.Empty(sagas); // a finalized instance is removed
        else Assert.Equal(state, Assert.Single(sagas).CurrentState);
    }

    protected async Task Purchase_with_enough_balance_debits_and_unlocks()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        await PurchaseAnswers202AfterTheTimeoutAsync(world, user, chapter);

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Completed);
        Assert.Null(tx.Note);
        Assert.Equal(380m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count);
        Assert.Equal((TransactionType.Purchase, -120m, (Guid?)tx.Id), ledger[1]);
        Assert.True(await world.ChapterUnlockedAsync(user, chapter));
        var purchase = await world.PaymentPurchaseAsync(user, chapter);
        Assert.NotNull(purchase);
        Assert.Equal(120m, purchase!.Price);
        await AssertSagaAsync(world, user, null,
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->UnlockingChapter", "UnlockingChapter->Final" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitedEvent", "UnlockChapterCommand", "ChapterUnlockedEvent", "PurchaseCompletedEvent" });
    }

    protected async Task Insufficient_balance_fails_without_charging()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 50m);

        await PurchaseAnswers202AfterTheTimeoutAsync(world, user, chapter);

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Failed);
        Assert.Equal("Số dư không đủ để thực hiện giao dịch.", tx.Note);
        Assert.Equal(50m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user)); // only the seed credit
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, "Failed",
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->Failed" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitFailedEvent", "PurchaseFailedEvent" });
    }

    protected async Task Wallet_debit_failure_fails_without_charging()
    {
        var world = await WorldAsync();
        world.FailWalletDebit = true;
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        await PurchaseAnswers202AfterTheTimeoutAsync(world, user, chapter);

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Failed);
        Assert.Equal("Lỗi hệ thống khi trừ DB: injected: wallet database failed", tx.Note);
        Assert.Equal(500m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user));
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, "Failed",
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->Failed" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitFailedEvent", "PurchaseFailedEvent" });
    }

    protected async Task Chapter_unlock_failure_refunds_the_debit()
    {
        var world = await WorldAsync();
        world.FailChapterUnlock = true;
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        await PurchaseAnswers202AfterTheTimeoutAsync(world, user, chapter);

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Failed);
        Assert.Equal("Lỗi hệ thống khi mở khóa chapter: injected: chapter database failed", tx.Note);
        Assert.Equal(500m, await world.BalanceAsync(user)); // debited, then refunded
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(3, ledger.Count);
        Assert.Equal((TransactionType.Purchase, -120m, (Guid?)tx.Id), ledger[1]);
        // The refund has its own reference id (never the debit's), deterministic per transaction.
        Assert.Equal((TransactionType.Refund, 120m, (Guid?)RefundReference(tx.Id)), ledger[2]);
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, "Failed",
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->UnlockingChapter", "UnlockingChapter->RefundingWallet", "RefundingWallet->Failed" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitedEvent", "UnlockChapterCommand", "ChapterUnlockFailedEvent", "RefundWalletCommand", "WalletRefundedEvent", "PurchaseFailedEvent" });
    }
}

/// <summary>Three separate buses over RabbitMQ with the EF Outbox: what the standalone APIs run today.</summary>
[Collection(T37Collection.Name)]
public class PurchaseSagaTests_RabbitMq : PurchaseSagaScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseSagaTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task KnownLimitation_Purchase_with_enough_balance_debits_and_unlocks_but_answers_202() => Purchase_with_enough_balance_debits_and_unlocks();
    [RabbitMqFact] public Task KnownLimitation_Insufficient_balance_fails_without_charging_but_answers_202() => Insufficient_balance_fails_without_charging();
    [RabbitMqFact] public Task KnownLimitation_Wallet_debit_failure_fails_without_charging_but_answers_202() => Wallet_debit_failure_fails_without_charging();
    [RabbitMqFact] public Task KnownLimitation_Chapter_unlock_failure_refunds_the_debit_but_answers_202() => Chapter_unlock_failure_refunds_the_debit();
}

/// <summary>One in-memory bus without outbox (the T20/T21 target).</summary>
[Collection(T37Collection.Name)]
public class PurchaseSagaTests_InMemory : PurchaseSagaScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseSagaTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task KnownLimitation_Purchase_with_enough_balance_debits_and_unlocks_but_answers_202() => Purchase_with_enough_balance_debits_and_unlocks();
    [Fact] public Task KnownLimitation_Insufficient_balance_fails_without_charging_but_answers_202() => Insufficient_balance_fails_without_charging();
    [Fact] public Task KnownLimitation_Wallet_debit_failure_fails_without_charging_but_answers_202() => Wallet_debit_failure_fails_without_charging();
    [Fact] public Task KnownLimitation_Chapter_unlock_failure_refunds_the_debit_but_answers_202() => Chapter_unlock_failure_refunds_the_debit();
}

/// <summary>The handler's catch-all path: the committed Purchase transaction is marked Failed, not inserted again.</summary>
[Collection(T37Collection.Name)]
public class SaveFailedTransactionTests
{
    private readonly T37Fixture _fixture;
    public SaveFailedTransactionTests(T37Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SaveFailedTransactionAsync_marks_the_committed_transaction_failed_without_inserting_it_again()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        using var scope = world.PaymentScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();

        // Same steps as PurchaseChapterCommandHandler: insert and commit, then its catch (Exception) block.
        await repository.BeginTransactionAsync();
        var transaction = new PaymentAPI.Entities.Transaction
        {
            UserId = user,
            ChapterId = Guid.NewGuid(),
            Type = TransactionType.Purchase,
            Amount = 120m,
            CurrencyType = "Dâu",
            Status = TransactionStatus.Pending,
        };
        await repository.AddTransactionAsync(transaction);
        await repository.CommitTransactionAsync();

        transaction.Status = TransactionStatus.Failed;
        transaction.Note = "Lỗi hệ thống khi khởi tạo giao dịch mua.";
        await repository.SaveFailedTransactionAsync(transaction);

        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(transaction.Id, tx.Id);
        Assert.Equal(TransactionStatus.Failed, tx.Status);
        Assert.Equal("Lỗi hệ thống khi khởi tạo giao dịch mua.", tx.Note);
        Assert.Equal(120m, tx.Amount);
    }
}
