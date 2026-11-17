using System.Diagnostics;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Services;
using PaymentAPI.Data;
using PaymentAPI.DTOs;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// T45: a second purchase of the same user and chapter while an earlier one is Pending or Completed.
/// Before T45 the handler only checked the Payment <c>UserPurchase</c>, written last by <c>PurchaseCompletedConsumer</c>,
/// so a second request inside that window inserted a second transaction, started a second saga and was debited again.
/// Since T45 the handler takes a SQL Server application lock for the user and chapter inside the transaction that
/// inserts the Pending row, and checks for a Pending or Completed purchase first: Pending answers 409, Completed answers
/// the existing "already unlocked" 200, and neither starts a saga.
/// </summary>
public abstract class DuplicatePurchaseScenarios
{
    protected abstract Task<TestWorld> WorldAsync();

    private const decimal Price = 120m;
    private const string PendingMessage = "Chapter này đang được thanh toán. Vui lòng chờ giao dịch trước hoàn tất.";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private sealed record Answer(int Status, ApiResponse<ChapterPurchaseResultDto> Body);

    private static async Task<Answer> PurchaseAsync(TestWorld world, int user, Guid chapter)
    {
        using var scope = world.PaymentScope();
        var controller = Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString());
        var result = await controller.PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = chapter });
        return new Answer(Controllers.StatusOf(result),
            Assert.IsType<ApiResponse<ChapterPurchaseResultDto>>(((ObjectResult)result).Value));
    }

    private static void AssertUnlockedNow(Answer answer)
    {
        Assert.Equal(200, answer.Status);
        Assert.Equal("Mở khóa chapter thành công.", answer.Body.Message);
        Assert.False(answer.Body.Data!.AlreadyPurchased);
    }

    private static void AssertPending(Answer answer)
    {
        Assert.Equal(409, answer.Status);
        Assert.False(answer.Body.Success);
        Assert.Equal(PendingMessage, answer.Body.Message);
        Assert.Null(answer.Body.Data);
    }

    private static void AssertAlreadyUnlocked(Answer answer)
    {
        Assert.Equal(200, answer.Status);
        Assert.True(answer.Body.Success);
        Assert.Equal("Chapter này đã được mở khóa trước đó.", answer.Body.Message);
        Assert.Equal(Price, answer.Body.Data!.Price);
        Assert.True(answer.Body.Data.AlreadyPurchased);
    }

    private static Task<bool> CompletedAsync(TestWorld world, int user, Guid chapter) =>
        TestWorld.EventuallyAsync(async () =>
            (await world.TransactionsAsync(user)).Any(t => t.Status == TransactionStatus.Completed)
            && await world.PaymentPurchaseAsync(user, chapter) != null);

    /// <summary>
    /// The money state after exactly one successful purchase: one debit, balance start - price, one unlock row,
    /// exactly one Completed transaction, no Pending one, and <paramref name="otherTransactions"/> other (Failed) rows.
    /// </summary>
    private static async Task AssertOnePurchaseAsync(TestWorld world, int user, Guid chapter, decimal start, int otherTransactions = 0)
    {
        Assert.True(await CompletedAsync(world, user, chapter), "The purchase never completed.");
        await Task.Delay(500); // let a second saga, if any, finish its debit

        var ledger = await world.LedgerAsync(user);
        Assert.Single(ledger, e => e.Type == TransactionType.Purchase);
        Assert.DoesNotContain(ledger, e => e.Type == TransactionType.Refund);
        Assert.Equal(start - Price, await world.BalanceAsync(user));

        using (var scope = world.ChapterScope())
        {
            var rows = scope.ServiceProvider.GetRequiredService<ChapterAPI.Data.ChapterDbContext>()
                .UserPurchases.Count(p => p.UserId == user && p.ChapterId == chapter);
            Assert.Equal(1, rows);
        }

        var transactions = await world.TransactionsAsync(user);
        Assert.Single(transactions, t => t.Status == TransactionStatus.Completed);
        Assert.DoesNotContain(transactions, t => t.Status == TransactionStatus.Pending);
        Assert.Equal(1 + otherTransactions, transactions.Count);
        Assert.NotNull(await world.PaymentPurchaseAsync(user, chapter));
    }

    /// <summary>Blocks the first chapter unlock (after the debit) until released; signals when it is reached.</summary>
    private sealed class UnlockGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UnlockGate(TestWorld world) =>
            world.BeforeChapterUnlock = () =>
            {
                Reached.TrySetResult();
                _release.Wait(Wait); // never hangs the bus for longer than this
            };

        public void Release() => _release.Set();
        public void Dispose() => _release.Set();
    }

    // (a) Two requests that both passed the first guard at the same time.
    protected async Task Two_concurrent_requests_debit_and_unlock_once()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 500m);

        // Hold both requests after GetChapterInfo (past the UserPurchase guard) until both are there.
        var arrived = 0;
        var bothThere = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Grpc.AfterChapterInfo = async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothThere.TrySetResult();
            await bothThere.Task.WaitAsync(Wait);
        };
        // The winner's saga waits at the unlock, so the loser's check certainly finds it Pending.
        using var gate = new UnlockGate(world);

        var first = PurchaseAsync(world, user, chapter);
        var second = PurchaseAsync(world, user, chapter);
        Task<Answer> loser;
        try
        {
            loser = await Task.WhenAny(first, second).WaitAsync(Wait);
        }
        finally
        {
            gate.Release();
        }
        var winner = loser == first ? second : first;

        Assert.Equal(2, arrived);
        AssertPending(await loser);
        AssertUnlockedNow(await winner.WaitAsync(Wait));
        await AssertOnePurchaseAsync(world, user, chapter, 500m);
    }

    // (b) A second request while the first is Pending (debited, unlock not yet done).
    protected async Task Second_request_while_the_first_is_pending_is_not_debited()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 500m);

        Answer second;
        Task<Answer> first;
        using (var gate = new UnlockGate(world))
        {
            first = PurchaseAsync(world, user, chapter);
            await gate.Reached.Task.WaitAsync(Wait);
            Assert.Single(await world.LedgerAsync(user), e => e.Type == TransactionType.Purchase); // first is debited

            var watch = Stopwatch.StartNew();
            try
            {
                second = await PurchaseAsync(world, user, chapter).WaitAsync(Wait);
            }
            finally
            {
                gate.Release();
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The second request waited {watch.Elapsed}.");
        }

        AssertPending(second);
        AssertUnlockedNow(await first.WaitAsync(Wait));
        await AssertOnePurchaseAsync(world, user, chapter, 500m);
    }

    // (c) A request after the first purchase is Completed: the existing first guard answers "already unlocked".
    protected async Task Request_after_completion_answers_already_unlocked_without_a_debit()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 500m);

        AssertUnlockedNow(await PurchaseAsync(world, user, chapter));
        Assert.True(await CompletedAsync(world, user, chapter), "The first purchase never completed.");

        AssertAlreadyUnlocked(await PurchaseAsync(world, user, chapter));
        await AssertOnePurchaseAsync(world, user, chapter, 500m);
    }

    // (c2) The first purchase completes while the second request is between the first guard and the lock:
    // the locked check finds the Completed transaction and answers "already unlocked".
    protected async Task Purchase_completing_while_the_second_waits_answers_already_unlocked()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 500m);

        using var gate = new UnlockGate(world);
        var first = PurchaseAsync(world, user, chapter);
        await gate.Reached.Task.WaitAsync(Wait);

        // The second request passed the first guard (no UserPurchase yet); now let the first purchase finish.
        world.Grpc.AfterChapterInfo = async () =>
        {
            world.Grpc.AfterChapterInfo = null;
            gate.Release();
            Assert.True(await CompletedAsync(world, user, chapter), "The first purchase never completed.");
        };
        var second = await PurchaseAsync(world, user, chapter).WaitAsync(Wait);

        AssertUnlockedNow(await first.WaitAsync(Wait));
        AssertAlreadyUnlocked(second);
        await AssertOnePurchaseAsync(world, user, chapter, 500m);
    }

    // (d) A Pending purchase stuck after a crash blocks a new purchase until the recovery job (T23) settles it.
    protected async Task Stuck_pending_purchase_blocks_until_the_recovery_job_fails_it()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 500m);

        // Crash before the debit: a Pending transaction older than the threshold, no saga row, no debit.
        var stuck = new PaymentAPI.Entities.Transaction
        {
            UserId = user,
            ChapterId = chapter,
            Type = TransactionType.Purchase,
            Amount = Price,
            CurrencyType = "Dâu",
            Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        };
        using (var scope = world.PaymentScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            db.Transactions.Add(stuck);
            await db.SaveChangesAsync();
        }

        AssertPending(await PurchaseAsync(world, user, chapter));
        Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(500m, await world.BalanceAsync(user));

        using (var scope = world.PaymentScope())
        {
            await scope.ServiceProvider.GetRequiredService<PurchaseRecoveryJob>().RunOnceAsync();
        }
        var failed = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(TransactionStatus.Failed, failed.Status);
        Assert.StartsWith(PurchaseRecoveryJob.NotePrefix, failed.Note);

        AssertUnlockedNow(await PurchaseAsync(world, user, chapter));
        await AssertOnePurchaseAsync(world, user, chapter, 500m, otherTransactions: 1);
    }

    // (e) After a Failed purchase (insufficient balance) the user can buy again.
    protected async Task Failed_purchase_does_not_block_buying_again()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(Price);
        await world.SeedWalletAsync(user, 50m);

        var refused = await PurchaseAsync(world, user, chapter);
        Assert.Equal(402, refused.Status);
        Assert.True(await TestWorld.EventuallyAsync(async () =>
            (await world.TransactionsAsync(user)).All(t => t.Status == TransactionStatus.Failed)), "The first purchase never failed.");

        await world.SeedWalletAsync(user, 450m);
        AssertUnlockedNow(await PurchaseAsync(world, user, chapter));
        await AssertOnePurchaseAsync(world, user, chapter, 500m, otherTransactions: 1);
    }
}

[Collection(T37Collection.Name)]
public class DuplicatePurchaseTests_InMemory : DuplicatePurchaseScenarios
{
    private readonly T37Fixture _fixture;
    public DuplicatePurchaseTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task Two_concurrent_requests_debit_and_unlock_once_and_answer_200_and_409() => Two_concurrent_requests_debit_and_unlock_once();
    [Fact] public Task Second_request_while_pending_answers_409_without_a_debit() => Second_request_while_the_first_is_pending_is_not_debited();
    [Fact] public Task Request_after_completion_answers_already_unlocked() => Request_after_completion_answers_already_unlocked_without_a_debit();
    [Fact] public Task Completion_during_the_second_request_answers_already_unlocked() => Purchase_completing_while_the_second_waits_answers_already_unlocked();
    [Fact] public Task Stuck_pending_purchase_answers_409_until_the_recovery_job_fails_it() => Stuck_pending_purchase_blocks_until_the_recovery_job_fails_it();
    [Fact] public Task Failed_purchase_can_be_bought_again() => Failed_purchase_does_not_block_buying_again();
}

[Collection(T37Collection.Name)]
public class DuplicatePurchaseTests_RabbitMq : DuplicatePurchaseScenarios
{
    private readonly T37Fixture _fixture;
    public DuplicatePurchaseTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task Two_concurrent_requests_debit_and_unlock_once_and_answer_200_and_409() => Two_concurrent_requests_debit_and_unlock_once();
    [RabbitMqFact] public Task Second_request_while_pending_answers_409_without_a_debit() => Second_request_while_the_first_is_pending_is_not_debited();
    [RabbitMqFact] public Task Request_after_completion_answers_already_unlocked() => Request_after_completion_answers_already_unlocked_without_a_debit();
    [RabbitMqFact] public Task Completion_during_the_second_request_answers_already_unlocked() => Purchase_completing_while_the_second_waits_answers_already_unlocked();
    [RabbitMqFact] public Task Stuck_pending_purchase_answers_409_until_the_recovery_job_fails_it() => Stuck_pending_purchase_blocks_until_the_recovery_job_fails_it();
    [RabbitMqFact] public Task Failed_purchase_can_be_bought_again() => Failed_purchase_does_not_block_buying_again();
}

[Collection(T37Collection.Name)]
public class DuplicatePurchaseTests_HostBusRabbitMq : DuplicatePurchaseScenarios
{
    private readonly T37Fixture _fixture;
    public DuplicatePurchaseTests_HostBusRabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.HostBusAsync();

    [RabbitMqFact] public Task Two_concurrent_requests_debit_and_unlock_once_and_answer_200_and_409() => Two_concurrent_requests_debit_and_unlock_once();
    [RabbitMqFact] public Task Second_request_while_pending_answers_409_without_a_debit() => Second_request_while_the_first_is_pending_is_not_debited();
    [RabbitMqFact] public Task Request_after_completion_answers_already_unlocked() => Request_after_completion_answers_already_unlocked_without_a_debit();
    [RabbitMqFact] public Task Completion_during_the_second_request_answers_already_unlocked() => Purchase_completing_while_the_second_waits_answers_already_unlocked();
    [RabbitMqFact] public Task Stuck_pending_purchase_answers_409_until_the_recovery_job_fails_it() => Stuck_pending_purchase_blocks_until_the_recovery_job_fails_it();
    [RabbitMqFact] public Task Failed_purchase_can_be_bought_again() => Failed_purchase_does_not_block_buying_again();
}
