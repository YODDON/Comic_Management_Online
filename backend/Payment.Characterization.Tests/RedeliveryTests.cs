using ChapterAPI.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using SharedKernel.Events;

namespace Payment.Characterization.Tests;

/// <summary>
/// T20: the money and unlock consumers must reject a redelivered message on their own, because the single host bus
/// runs without the MassTransit EF Outbox/Inbox (the old services only had the publish-side bus outbox; no receive
/// endpoint ever used the inbox). Each message is sent twice to its real queue with the SAME MessageId and payload,
/// exactly what a broker redelivery looks like. The second delivery must not add a ledger entry or an unlock, and
/// the balance must not change. Expected values were observed on the setup before T20 changed the bus.
/// </summary>
public abstract class RedeliveryScenarios
{
    protected abstract Task<TestWorld> WorldAsync();

    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Completes when the consumer has finished (or faulted on) the message with the given id.</summary>
    private sealed class ConsumeWatcher<T> : IConsumeObserver where T : class
    {
        private readonly Guid _messageId;
        private readonly TaskCompletionSource<Exception?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConsumeWatcher(Guid messageId) => _messageId = messageId;
        public Task<Exception?> Done => _done.Task;

        public Task PreConsume<TM>(ConsumeContext<TM> context) where TM : class => Task.CompletedTask;

        public Task PostConsume<TM>(ConsumeContext<TM> context) where TM : class
        {
            if (Matches(context)) _done.TrySetResult(null);
            return Task.CompletedTask;
        }

        public Task ConsumeFault<TM>(ConsumeContext<TM> context, Exception exception) where TM : class
        {
            if (Matches(context)) _done.TrySetResult(exception);
            return Task.CompletedTask;
        }

        private bool Matches<TM>(ConsumeContext<TM> context) where TM : class =>
            typeof(TM) == typeof(T) && context.MessageId == _messageId;
    }

    /// <summary>Sends the message to the queue and waits until the consumer has finished it without a fault.</summary>
    private static async Task DeliverAsync<T>(IServiceScope consumerSide, string queue, T message, Guid messageId) where T : class
    {
        var bus = consumerSide.ServiceProvider.GetRequiredService<IBus>();
        var watcher = new ConsumeWatcher<T>(messageId);
        var handle = bus.ConnectConsumeObserver(watcher);
        try
        {
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{queue}"));
            await endpoint.Send(message, (SendContext<T> context) => context.MessageId = messageId);

            var finished = await Task.WhenAny(watcher.Done, Task.Delay(ConsumeTimeout));
            Assert.True(finished == watcher.Done, $"{typeof(T).Name} {messageId} was not consumed within {ConsumeTimeout}.");
            Assert.Null(await watcher.Done); // the consumer did not fault
        }
        finally
        {
            handle.Disconnect();
        }
    }

    /// <summary>
    /// The consumers also publish their result event (to the saga, which has no instance for these test ids).
    /// Wait until both copies were consumed so nothing from this test arrives during the next one.
    /// </summary>
    private static async Task DrainAsync(TestWorld world, string resultEvent)
    {
        await TestWorld.EventuallyAsync(() => Task.FromResult(world.Messages.Snapshot().Count(m => m == resultEvent) >= 2),
            TimeSpan.FromSeconds(5));
        await Task.Delay(500);
    }

    private static Guid RefundReference(Guid transactionId)
    {
        var bytes = transactionId.ToByteArray();
        bytes[15] ^= 0xFF;
        return new Guid(bytes);
    }

    private static async Task<int> UnlockRowsAsync(TestWorld world, int userId, Guid chapterId)
    {
        using var scope = world.ChapterScope();
        var db = scope.ServiceProvider.GetRequiredService<ChapterDbContext>();
        return await db.UserPurchases.CountAsync(p => p.UserId == userId && p.ChapterId == chapterId);
    }

    protected async Task DebitWallet_redelivered_debits_once()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, 500m);
        var transactionId = Guid.NewGuid();
        var message = new DebitWalletCommand(Guid.NewGuid(), user, 120m, transactionId);
        var messageId = Guid.NewGuid();

        using var scope = world.WalletScope();
        await DeliverAsync(scope, "debit-wallet", message, messageId);
        Assert.Equal(380m, await world.BalanceAsync(user));

        await DeliverAsync(scope, "debit-wallet", message, messageId);
        Assert.Equal(380m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count); // seed + one debit
        Assert.Equal((TransactionType.Purchase, -120m, (Guid?)transactionId), ledger[1]);

        await DrainAsync(world, nameof(WalletDebitedEvent));
    }

    protected async Task RefundWallet_redelivered_refunds_once()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, 500m);
        var transactionId = Guid.NewGuid();
        var message = new RefundWalletCommand(Guid.NewGuid(), user, 120m, transactionId);
        var messageId = Guid.NewGuid();

        using var scope = world.WalletScope();
        await DeliverAsync(scope, "refund-wallet", message, messageId);
        Assert.Equal(620m, await world.BalanceAsync(user));

        await DeliverAsync(scope, "refund-wallet", message, messageId);
        Assert.Equal(620m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count); // seed + one refund
        Assert.Equal((TransactionType.Refund, 120m, (Guid?)RefundReference(transactionId)), ledger[1]);

        await DrainAsync(world, nameof(WalletRefundedEvent));
    }

    protected async Task MissionRewardGranted_redelivered_credits_once()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        await world.SeedWalletAsync(user, 500m);
        var referenceId = Guid.NewGuid();
        var message = new MissionRewardGrantedEvent
        {
            UserId = user,
            CoinAmount = 50m,
            ReferenceId = referenceId,
            Description = "T20 redelivery",
        };
        var messageId = Guid.NewGuid();

        using var scope = world.WalletScope();
        await DeliverAsync(scope, "mission-reward-granted", message, messageId);
        Assert.Equal(550m, await world.BalanceAsync(user));

        await DeliverAsync(scope, "mission-reward-granted", message, messageId);
        Assert.Equal(550m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count); // seed + one reward
        Assert.Equal((TransactionType.MissionReward, 50m, (Guid?)referenceId), ledger[1]);
    }

    protected async Task UnlockChapter_redelivered_unlocks_once()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        var message = new UnlockChapterCommand(Guid.NewGuid(), user, chapter);
        var messageId = Guid.NewGuid();

        using var scope = world.ChapterScope();
        await DeliverAsync(scope, "unlock-chapter", message, messageId);
        Assert.Equal(1, await UnlockRowsAsync(world, user, chapter));

        await DeliverAsync(scope, "unlock-chapter", message, messageId);
        Assert.Equal(1, await UnlockRowsAsync(world, user, chapter));

        await DrainAsync(world, nameof(ChapterUnlockedEvent));
    }
}

[Collection(T37Collection.Name)]
public class RedeliveryTests_RabbitMq : RedeliveryScenarios
{
    private readonly T37Fixture _fixture;
    public RedeliveryTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task DebitWallet_redelivered_debits_once_and_keeps_the_balance() => DebitWallet_redelivered_debits_once();
    [RabbitMqFact] public Task RefundWallet_redelivered_refunds_once_and_keeps_the_balance() => RefundWallet_redelivered_refunds_once();
    [RabbitMqFact] public Task MissionRewardGranted_redelivered_credits_once_and_keeps_the_balance() => MissionRewardGranted_redelivered_credits_once();
    [RabbitMqFact] public Task UnlockChapter_redelivered_adds_one_unlock() => UnlockChapter_redelivered_unlocks_once();
}

[Collection(T37Collection.Name)]
public class RedeliveryTests_InMemory : RedeliveryScenarios
{
    private readonly T37Fixture _fixture;
    public RedeliveryTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task DebitWallet_redelivered_debits_once_and_keeps_the_balance() => DebitWallet_redelivered_debits_once();
    [Fact] public Task RefundWallet_redelivered_refunds_once_and_keeps_the_balance() => RefundWallet_redelivered_refunds_once();
    [Fact] public Task MissionRewardGranted_redelivered_credits_once_and_keeps_the_balance() => MissionRewardGranted_redelivered_credits_once();
    [Fact] public Task UnlockChapter_redelivered_adds_one_unlock() => UnlockChapter_redelivered_unlocks_once();
}

/// <summary>The monolith host's single RabbitMQ bus without outbox, built from the modules' own registrations (T20).</summary>
[Collection(T37Collection.Name)]
public class RedeliveryTests_HostBusRabbitMq : RedeliveryScenarios
{
    private readonly T37Fixture _fixture;
    public RedeliveryTests_HostBusRabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.HostBusAsync();

    [RabbitMqFact] public Task DebitWallet_redelivered_debits_once_and_keeps_the_balance() => DebitWallet_redelivered_debits_once();
    [RabbitMqFact] public Task RefundWallet_redelivered_refunds_once_and_keeps_the_balance() => RefundWallet_redelivered_refunds_once();
    [RabbitMqFact] public Task MissionRewardGranted_redelivered_credits_once_and_keeps_the_balance() => MissionRewardGranted_redelivered_credits_once();
    [RabbitMqFact] public Task UnlockChapter_redelivered_adds_one_unlock() => UnlockChapter_redelivered_unlocks_once();
}
