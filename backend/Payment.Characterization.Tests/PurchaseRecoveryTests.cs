using ChapterAPI.Interfaces;
using ChapterAPI.Data;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Services;
using PaymentAPI.Data;
using PaymentAPI.Domain.Entities;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using WalletAPI.Interfaces;

namespace Payment.Characterization.Tests;

/// <summary>
/// T23: the purchase recovery job after a crash mid-purchase. With the in-memory bus a crash loses every saga message
/// in flight; what survives is the database: the Pending transaction, the saga row in its last state, the wallet ledger
/// and the chapter unlock row. Each test writes exactly that state through the real repositories (debit with
/// <c>WalletRepository.DebitAsync</c> and the transaction id as reference, like <c>DebitWalletConsumer</c>; unlock with
/// <c>ChapterRepository.UnlockChapterAsync</c> + save, like <c>UnlockChapterConsumer</c>), backdated past the age
/// threshold, then runs the job TWICE and checks the money state: the second run must change nothing.
/// The database is shared with the other T37 tests; their purchases are younger than the threshold and are not touched.
/// </summary>
[Collection(T37Collection.Name)]
public class PurchaseRecoveryTests
{
    private const decimal Start = 500m;
    private const decimal Price = 120m;
    private static readonly TimeSpan Old = TimeSpan.FromMinutes(30); // default threshold is 10 minutes

    private readonly T37Fixture _fixture;

    public PurchaseRecoveryTests(T37Fixture fixture) => _fixture = fixture;

    // ---------- crash points ----------

    [Fact]
    public async Task Crash_before_the_debit_fails_the_purchase_without_any_refund()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "ProcessingPayment", debited: false, unlocked: false);

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: false);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        Assert.Single(await world.LedgerAsync(p.User)); // only the seed top-up
    }

    [Fact]
    public async Task Crash_after_the_debit_before_the_unlock_refunds_exactly_once()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false);

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: true);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
        Assert.False(await world.ChapterUnlockedAsync(p.User, p.Chapter));
    }

    [Fact]
    public async Task Crash_after_the_unlock_before_the_event_completes_the_purchase_without_refund()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: true);

        await RunTwiceAsync(world);

        var tx = await TransactionAsync(world, p);
        Assert.Equal(TransactionStatus.Completed, tx.Status);
        Assert.Null(tx.Note);
        var purchase = await world.PaymentPurchaseAsync(p.User, p.Chapter);
        Assert.NotNull(purchase);
        Assert.Equal(p.Comic, purchase!.ComicId);
        Assert.Equal(Price, purchase.Price);
        Assert.Null(await world.SagaAsync(p.Correlation)); // removed, like the saga's Finalize
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 0);
        Assert.True(await world.ChapterUnlockedAsync(p.User, p.Chapter));
    }

    [Fact]
    public async Task Crash_in_RefundingWallet_before_the_refund_refunds_exactly_once()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "RefundingWallet", debited: true, unlocked: false);

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: true);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
    }

    [Fact]
    public async Task Crash_in_RefundingWallet_after_the_refund_does_not_refund_again()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "RefundingWallet", debited: true, unlocked: false, refundedBySaga: true);

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: true);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
    }

    [Fact]
    public async Task Pending_purchase_without_a_saga_row_that_was_debited_and_unlocked_is_completed()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, sagaState: null, debited: true, unlocked: true);

        await RunTwiceAsync(world);

        Assert.Equal(TransactionStatus.Completed, (await TransactionAsync(world, p)).Status);
        var purchase = await world.PaymentPurchaseAsync(p.User, p.Chapter);
        Assert.NotNull(purchase);
        Assert.Equal(p.Comic, purchase!.ComicId); // from the chapter info, there is no saga to read it from
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
    }

    // ---------- conditions ----------

    [Fact]
    public async Task Refund_amount_is_the_debit_entry_amount_not_the_price()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false, debitAmount: 70m);

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: true);
        var refund = Assert.Single(await world.LedgerAsync(p.User), e => e.Type == TransactionType.Refund);
        Assert.Equal(70m, refund.Amount);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
    }

    [Fact]
    public async Task Unreadable_debit_entry_is_critical_and_leaves_everything_as_it_is()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false);
        lock (world.Grpc.Calls) world.Grpc.Calls.Clear();
        world.Grpc.Unavailable.Add(ModuleCallLog.GetLedgerEntry);
        try
        {
            await RunTwiceAsync(world);
        }
        finally
        {
            world.Grpc.Unavailable.Clear();
        }

        Assert.Equal(TransactionStatus.Pending, (await TransactionAsync(world, p)).Status);
        Assert.Equal("UnlockingChapter", (await world.SagaAsync(p.Correlation))!.CurrentState);
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 0);
        Assert.DoesNotContain(ModuleCallLog.RefundPurchase, world.Grpc.Calls);
    }

    [Fact]
    public async Task Purchase_younger_than_the_threshold_is_not_touched()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false, age: TimeSpan.FromMinutes(1));

        await RunTwiceAsync(world);

        Assert.Equal(TransactionStatus.Pending, (await TransactionAsync(world, p)).Status);
        Assert.Equal("UnlockingChapter", (await world.SagaAsync(p.Correlation))!.CurrentState);
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 0);
    }

    [Fact]
    public async Task Saga_with_a_recent_step_is_not_touched_even_when_the_transaction_is_old()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false, sagaUpdatedAgo: TimeSpan.FromSeconds(5));

        await RunTwiceAsync(world);

        Assert.Equal(TransactionStatus.Pending, (await TransactionAsync(world, p)).Status);
        await AssertLedgerAsync(world, p, refunds: 0);
    }

    [Fact]
    public async Task Debit_arriving_after_the_job_failed_the_purchase_is_refunded_once()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "ProcessingPayment", debited: false, unlocked: false);
        await RunTwiceAsync(world);
        await AssertFailedAsync(world, p, refunded: false);

        // The DebitWalletCommand was only slow, not lost: the debit lands after the job failed the purchase.
        await DebitAsync(world, p, Price);
        await RunTwiceAsync(world);

        Assert.Equal(TransactionStatus.Failed, (await TransactionAsync(world, p)).Status);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
    }

    [Fact]
    public async Task Two_concurrent_runs_still_refund_exactly_once()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false);
        using var scope = world.PaymentScope();
        var job = scope.ServiceProvider.GetRequiredService<PurchaseRecoveryJob>();

        await Task.WhenAll(job.RunOnceAsync(), job.RunOnceAsync());
        await RunTwiceAsync(world); // a run that lost a race is settled by the next one

        await AssertFailedAsync(world, p, refunded: true);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
    }

    [Fact]
    public async Task Chapter_unlocked_between_the_read_and_the_lock_is_not_refunded()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "UnlockingChapter", debited: true, unlocked: false);

        // UnlockChapterConsumer saves the unlock row right after the job read "not unlocked". The saga row does not
        // change until ChapterUnlockedEvent is processed, so only the locked re-check of the unlock row can see it.
        var done = false;
        world.Grpc.AfterUnlockCheck = async (user, chapter) =>
        {
            if (done || user != p.User) return;
            done = true;
            await UnlockAsync(world, p.User, p.Chapter);
        };
        try
        {
            using var scope = world.PaymentScope();
            await scope.ServiceProvider.GetRequiredService<PurchaseRecoveryJob>().RunOnceAsync();
        }
        finally
        {
            world.Grpc.AfterUnlockCheck = null;
        }

        Assert.True(done);
        Assert.Equal(TransactionStatus.Pending, (await TransactionAsync(world, p)).Status);
        Assert.Equal("UnlockingChapter", (await world.SagaAsync(p.Correlation))!.CurrentState);
        await AssertLedgerAsync(world, p, refunds: 0);

        // The next runs see debited and unlocked: Completed, still no refund.
        await RunTwiceAsync(world);
        Assert.Equal(TransactionStatus.Completed, (await TransactionAsync(world, p)).Status);
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 0);
    }

    [Fact]
    public async Task Completion_without_a_known_comic_id_is_critical_and_records_nothing()
    {
        var world = await _fixture.InMemoryAsync();
        Guid chapter;
        using (var scope = world.ChapterScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChapterDbContext>();
            var row = new ChapterAPI.Entities.Chapter
            {
                ComicId = Guid.Empty,
                Title = "T23 chapter without comic",
                Slug = $"t23-{Guid.NewGuid():N}",
                ChapterNumber = 1,
                UnitPrice = Price,
                Status = "Published",
            };
            db.Chapters.Add(row);
            await db.SaveChangesAsync();
            chapter = row.Id;
        }

        // No saga row (no ComicId there) and the chapter info has an empty ComicId.
        var p = await SeedAsync(world, sagaState: null, debited: true, unlocked: true, chapterId: chapter);

        await RunTwiceAsync(world);

        Assert.Equal(TransactionStatus.Pending, (await TransactionAsync(world, p)).Status);
        Assert.Null(await world.PaymentPurchaseAsync(p.User, p.Chapter));
        Assert.Equal(Start - Price, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 0);
    }

    [Fact]
    public void Refund_reference_is_the_one_RefundWalletConsumer_uses()
    {
        var transactionId = Guid.NewGuid();
        var bytes = transactionId.ToByteArray(); // RefundWalletConsumer's own computation
        bytes[15] ^= 0xFF;

        Assert.Equal(new Guid(bytes), PurchaseReferenceIds.RefundOf(transactionId));
        Assert.Equal(transactionId, PurchaseReferenceIds.RefundOf(PurchaseReferenceIds.RefundOf(transactionId)));
    }

    [Fact]
    public async Task Refund_made_by_the_saga_and_by_the_job_is_applied_once()
    {
        var world = await _fixture.InMemoryAsync();
        var p = await SeedAsync(world, "RefundingWallet", debited: true, unlocked: false);

        // The saga's refund consumer and the job's refund race for the same purchase.
        using (var scope = world.WalletScope())
        {
            var consumerRepository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            await consumerRepository.CreditAsync(p.User, Price, PurchaseReferenceIds.RefundOf(p.Transaction),
                TransactionType.Refund, $"Hoàn tiền do lỗi mở chapter, Transaction: {p.Transaction}");
        }

        await RunTwiceAsync(world);

        await AssertFailedAsync(world, p, refunded: true);
        Assert.Equal(Start, await world.BalanceAsync(p.User));
        await AssertLedgerAsync(world, p, refunds: 1);
    }

    // ---------- helpers ----------

    private sealed record Purchase(int User, Guid Chapter, Guid Comic, Guid Transaction, Guid Correlation);

    private static async Task RunTwiceAsync(TestWorld world)
    {
        using var scope = world.PaymentScope();
        var job = scope.ServiceProvider.GetRequiredService<PurchaseRecoveryJob>();
        await job.RunOnceAsync();
        await job.RunOnceAsync();
    }

    /// <summary>The database state a crash leaves behind at a given saga step.</summary>
    private static async Task<Purchase> SeedAsync(
        TestWorld world, string? sagaState, bool debited, bool unlocked,
        bool refundedBySaga = false, decimal? debitAmount = null, TimeSpan? age = null, TimeSpan? sagaUpdatedAgo = null,
        Guid? chapterId = null)
    {
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, Start);
        var chapter = chapterId ?? await world.SeedChapterAsync(Price);
        Guid comic;
        using (var scope = world.PaymentScope())
        {
            comic = (await scope.ServiceProvider
                .GetRequiredService<SharedKernel.Contracts.Modules.IPaymentChapterGateway>().GetChapterInfoAsync(chapter)).ComicId;
        }

        var createdAt = DateTime.UtcNow - (age ?? Old);

        var transaction = new PaymentAPI.Entities.Transaction
        {
            UserId = user,
            ChapterId = chapter,
            Type = TransactionType.Purchase,
            Amount = Price,
            CurrencyType = "Dâu",
            Status = TransactionStatus.Pending,
            CreatedAt = createdAt,
        };
        var correlation = Guid.NewGuid();
        using (var scope = world.PaymentScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            db.Transactions.Add(transaction);
            if (sagaState != null)
            {
                db.PurchaseStates.Add(new PurchaseState
                {
                    CorrelationId = correlation,
                    CurrentState = sagaState,
                    UserId = user,
                    ComicId = comic,
                    ChapterId = chapter,
                    Price = Price,
                    TransactionId = transaction.Id,
                    CreatedAt = createdAt,
                    UpdatedAt = DateTime.UtcNow - (sagaUpdatedAgo ?? age ?? Old),
                });
            }

            await db.SaveChangesAsync();
        }

        var p = new Purchase(user, chapter, comic, transaction.Id, correlation);
        if (debited) await DebitAsync(world, p, debitAmount ?? Price);

        if (unlocked) await UnlockAsync(world, user, chapter);

        if (refundedBySaga)
        {
            using var scope = world.WalletScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            await repository.CreditAsync(user, debitAmount ?? Price, PurchaseReferenceIds.RefundOf(transaction.Id),
                TransactionType.Refund, $"Hoàn tiền do lỗi mở chapter, Transaction: {transaction.Id}");
        }

        return p;
    }

    /// <summary>Same calls as UnlockChapterConsumer: add the unlock row, then save.</summary>
    private static async Task UnlockAsync(TestWorld world, int user, Guid chapter)
    {
        using var scope = world.ChapterScope();
        var repository = scope.ServiceProvider.GetRequiredService<IChapterRepository>();
        Assert.True(await repository.UnlockChapterAsync(user, chapter));
        await repository.SaveChangesAsync();
    }

    /// <summary>Same call as DebitWalletConsumer: the transaction id is the ledger reference.</summary>
    private static async Task DebitAsync(TestWorld world, Purchase p, decimal amount)
    {
        using var scope = world.WalletScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var result = await repository.DebitAsync(p.User, amount, p.Transaction, $"Mở khóa chapter qua Saga, Transaction: {p.Transaction}");
        Assert.True(result.Success);
    }

    private static async Task<PaymentAPI.Entities.Transaction> TransactionAsync(TestWorld world, Purchase p) =>
        Assert.Single(await world.TransactionsAsync(p.User), t => t.Id == p.Transaction);

    private static async Task AssertFailedAsync(TestWorld world, Purchase p, bool refunded)
    {
        var tx = await TransactionAsync(world, p);
        Assert.Equal(TransactionStatus.Failed, tx.Status);
        Assert.StartsWith(PurchaseRecoveryJob.NotePrefix, tx.Note);
        Assert.Null(await world.PaymentPurchaseAsync(p.User, p.Chapter));

        var saga = await world.SagaAsync(p.Correlation);
        if (saga != null) Assert.Equal("Failed", saga.CurrentState);

        Assert.Equal(refunded, (await world.LedgerAsync(p.User)).Any(e => e.ReferenceId == PurchaseReferenceIds.RefundOf(p.Transaction)));
    }

    /// <summary>One seed top-up, at most one debit for this purchase, exactly <paramref name="refunds"/> refunds.</summary>
    private static async Task AssertLedgerAsync(TestWorld world, Purchase p, int refunds)
    {
        var ledger = await world.LedgerAsync(p.User);
        Assert.Single(ledger, e => e.Type == TransactionType.ManualTopUp);
        Assert.Single(ledger, e => e.ReferenceId == p.Transaction && e.Type == TransactionType.Purchase);
        Assert.Equal(refunds, ledger.Count(e => e.Type == TransactionType.Refund));
        Assert.Equal(refunds, ledger.Count(e => e.ReferenceId == PurchaseReferenceIds.RefundOf(p.Transaction)));
    }
}
