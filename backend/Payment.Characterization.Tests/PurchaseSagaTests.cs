using System.Diagnostics;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.DTOs;
using PaymentAPI.Interfaces;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// Chapter purchase through the PurchaseStateMachine saga, after T39 fixed the contracts (parameterless constructors
/// for context.Init&lt;T&gt;), SaveFailedTransactionAsync (updates instead of re-inserting) and the refund reference id.
/// Expected values were observed by running the fixed code.
///
/// T41: the saga stores the RequestId and ResponseAddress of the SubmitPurchaseCommand request and answers it from the
/// final transition (success, insufficient balance, wallet failure, refund after an unlock failure), so the endpoint
/// answers within seconds with the real outcome. Before T41 every purchase answered HTTP 202 after the 30 s request
/// timeout. The result is also still published, which is what updates the Purchase transaction.
/// </summary>
public abstract class PurchaseSagaScenarios
{
    protected abstract Task<TestWorld> WorldAsync();

    // Far below the 30 s request timeout that every purchase hit before T41.
    private static readonly TimeSpan FastAnswer = TimeSpan.FromSeconds(10);

    private static async Task<ApiResponse<ChapterPurchaseResultDto>> PurchaseAsync(TestWorld world, int user, Guid chapter, int expectedStatus)
    {
        using var scope = world.PaymentScope();
        var controller = Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString());

        var watch = Stopwatch.StartNew();
        var result = await controller.PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = chapter });
        watch.Stop();

        Assert.True(watch.Elapsed < FastAnswer, $"Answered after {watch.Elapsed}, expected the saga's answer well before the 30 s request timeout.");
        Assert.Equal(expectedStatus, Controllers.StatusOf(result));
        return Assert.IsType<ApiResponse<ChapterPurchaseResultDto>>(((ObjectResult)result).Value);
    }

    private static async Task PurchaseSucceedsAsync(TestWorld world, int user, Guid chapter)
    {
        var body = await PurchaseAsync(world, user, chapter, 200);
        Assert.True(body.Success);
        Assert.Equal("Mở khóa chapter thành công.", body.Message);
        Assert.NotNull(body.Data);
        Assert.Equal(chapter, body.Data!.ChapterId);
        Assert.Equal(120m, body.Data.Price);
        Assert.Null(body.Data.RemainingBalance); // the saga sends -1 (unknown); the handler answers null, never a negative balance
        Assert.False(body.Data.AlreadyPurchased);
    }

    private static async Task PurchaseFailsAsync(TestWorld world, int user, Guid chapter, string reason)
    {
        var body = await PurchaseAsync(world, user, chapter, 402);
        Assert.False(body.Success);
        Assert.Equal($"Mở khóa chapter thất bại: {reason}", body.Message);
        Assert.Null(body.Data);
    }

    // A saga left in Failed keeps the request it answered.
    private static async Task AssertRequestStoredAsync(TestWorld world, int user)
    {
        var saga = Assert.Single(await world.SagasForAsync(user));
        Assert.NotNull(saga.RequestId);
        Assert.NotNull(saga.ResponseAddress);
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

    // When a request started the purchase, the saga's result is consumed twice: the published copy by
    // PurchaseCompletedConsumer / PurchaseFailedConsumer, and the response by the request client (exactly one each).
    // Without a requester it is consumed once (published copy only).
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

        await PurchaseSucceedsAsync(world, user, chapter);

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
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitedEvent", "UnlockChapterCommand", "ChapterUnlockedEvent", "PurchaseCompletedEvent", "PurchaseCompletedEvent" });
    }

    protected async Task Insufficient_balance_fails_without_charging()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 50m);

        await PurchaseFailsAsync(world, user, chapter, "Số dư không đủ để thực hiện giao dịch.");

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Failed);
        Assert.Equal("Số dư không đủ để thực hiện giao dịch.", tx.Note);
        Assert.Equal(50m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user)); // only the seed credit
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, "Failed",
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->Failed" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitFailedEvent", "PurchaseFailedEvent", "PurchaseFailedEvent" });
        await AssertRequestStoredAsync(world, user);
    }

    protected async Task Wallet_debit_failure_fails_without_charging()
    {
        var world = await WorldAsync();
        world.FailWalletDebit = true;
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        await PurchaseFailsAsync(world, user, chapter, "Lỗi hệ thống khi trừ DB: injected: wallet database failed");

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Failed);
        Assert.Equal("Lỗi hệ thống khi trừ DB: injected: wallet database failed", tx.Note);
        Assert.Equal(500m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user));
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, "Failed",
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->Failed" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitFailedEvent", "PurchaseFailedEvent", "PurchaseFailedEvent" });
        await AssertRequestStoredAsync(world, user);
    }

    protected async Task Chapter_unlock_failure_refunds_the_debit()
    {
        var world = await WorldAsync();
        world.FailChapterUnlock = true;
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        await PurchaseFailsAsync(world, user, chapter, "Lỗi hệ thống khi mở khóa chapter: injected: chapter database failed");

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
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitedEvent", "UnlockChapterCommand", "ChapterUnlockFailedEvent", "RefundWalletCommand", "WalletRefundedEvent", "PurchaseFailedEvent", "PurchaseFailedEvent" });
        await AssertRequestStoredAsync(world, user);
    }

    // Contract check: the same call as PurchaseChapterCommandHandler (IRequestClient<SubmitPurchaseCommand>
    // .GetResponse<PurchaseCompletedEvent, PurchaseFailedEvent>), with a 5 s timeout instead of 30 s. If the saga
    // answers with any other message type, the request client never matches it and this fails with a timeout.
    protected async Task Saga_response_types_match_the_request_client()
    {
        var world = await WorldAsync();
        var chapter = await world.SeedChapterAsync(120m);
        var comic = Guid.NewGuid();
        var rich = TestWorld.NewUserId();
        var poor = TestWorld.NewUserId();
        await world.SeedWalletAsync(rich, 500m);
        await world.SeedWalletAsync(poor, 50m);

        using var scope = world.PaymentScope();
        var client = scope.ServiceProvider.GetRequiredService<IRequestClient<SubmitPurchaseCommand>>();

        async Task<Response<PurchaseCompletedEvent, PurchaseFailedEvent>> AskAsync(int user)
        {
            var command = new SubmitPurchaseCommand(Guid.NewGuid(), user, comic, chapter, 120m, Guid.NewGuid());
            return await client.GetResponse<PurchaseCompletedEvent, PurchaseFailedEvent>(command, default, RequestTimeout.After(s: 5));
        }

        var success = await AskAsync(rich);
        Assert.True(success.Is(out Response<PurchaseCompletedEvent>? completed));
        Assert.Equal(comic, completed!.Message.ComicId);

        var failure = await AskAsync(poor);
        Assert.True(failure.Is(out Response<PurchaseFailedEvent>? failed));
        Assert.Equal("Số dư không đủ để thực hiện giao dịch.", failed!.Message.Reason);
    }

    // Same as a saga created before T41 (RequestId / ResponseAddress columns null): no one to answer, the saga still
    // finishes and the published result still completes the Purchase transaction.
    protected async Task Purchase_without_a_requester_still_completes()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        var transaction = new PaymentAPI.Entities.Transaction
        {
            UserId = user,
            ChapterId = chapter,
            Type = TransactionType.Purchase,
            Amount = 120m,
            CurrencyType = "Dâu",
            Status = TransactionStatus.Pending,
        };
        using (var scope = world.PaymentScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
            await repository.BeginTransactionAsync();
            await repository.AddTransactionAsync(transaction);
            await repository.CommitTransactionAsync();

            // Published straight on the bus: no RequestId, no ResponseAddress.
            await scope.ServiceProvider.GetRequiredService<IBus>().Publish(
                new SubmitPurchaseCommand(Guid.NewGuid(), user, Guid.NewGuid(), chapter, 120m, transaction.Id));
        }

        var tx = await FinalTransactionAsync(world, user, TransactionStatus.Completed);
        Assert.Equal(transaction.Id, tx.Id);
        Assert.Equal(380m, await world.BalanceAsync(user));
        Assert.True(await world.ChapterUnlockedAsync(user, chapter));
        Assert.NotNull(await world.PaymentPurchaseAsync(user, chapter));
        await AssertSagaAsync(world, user, null,
            new[] { "(none)->Initial", "Initial->ProcessingPayment", "ProcessingPayment->UnlockingChapter", "UnlockingChapter->Final" },
            new[] { "SubmitPurchaseCommand", "DebitWalletCommand", "WalletDebitedEvent", "UnlockChapterCommand", "ChapterUnlockedEvent", "PurchaseCompletedEvent" });
    }
}

/// <summary>Three separate buses over RabbitMQ with the EF Outbox: what the standalone APIs run today.</summary>
[Collection(T37Collection.Name)]
public class PurchaseSagaTests_RabbitMq : PurchaseSagaScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseSagaTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task Purchase_with_enough_balance_debits_unlocks_and_answers_200() => Purchase_with_enough_balance_debits_and_unlocks();
    [RabbitMqFact] public Task Insufficient_balance_fails_without_charging_and_answers_402() => Insufficient_balance_fails_without_charging();
    [RabbitMqFact] public Task Wallet_debit_failure_fails_without_charging_and_answers_402() => Wallet_debit_failure_fails_without_charging();
    [RabbitMqFact] public Task Chapter_unlock_failure_refunds_the_debit_and_answers_402() => Chapter_unlock_failure_refunds_the_debit();
    [RabbitMqFact] public Task Purchase_without_a_requester_still_completes_the_transaction() => Purchase_without_a_requester_still_completes();
    [RabbitMqFact] public Task Saga_answers_with_the_types_the_handler_awaits() => Saga_response_types_match_the_request_client();
}

/// <summary>One in-memory bus without outbox (the T20/T21 target).</summary>
[Collection(T37Collection.Name)]
public class PurchaseSagaTests_InMemory : PurchaseSagaScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseSagaTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task Purchase_with_enough_balance_debits_unlocks_and_answers_200() => Purchase_with_enough_balance_debits_and_unlocks();
    [Fact] public Task Insufficient_balance_fails_without_charging_and_answers_402() => Insufficient_balance_fails_without_charging();
    [Fact] public Task Wallet_debit_failure_fails_without_charging_and_answers_402() => Wallet_debit_failure_fails_without_charging();
    [Fact] public Task Chapter_unlock_failure_refunds_the_debit_and_answers_402() => Chapter_unlock_failure_refunds_the_debit();
    [Fact] public Task Purchase_without_a_requester_still_completes_the_transaction() => Purchase_without_a_requester_still_completes();
    [Fact] public Task Saga_answers_with_the_types_the_handler_awaits() => Saga_response_types_match_the_request_client();
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
