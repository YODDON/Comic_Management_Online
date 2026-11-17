using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.DTOs;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;

namespace Payment.Characterization.Tests;

/// <summary>
/// T37 characterization of a KNOWN BUG in the chapter purchase / PurchaseStateMachine saga.
///
/// What the current code does (observed by running it, identical on the original T01 copy f5aaf5a and on main,
/// on three separate buses over RabbitMQ with the EF Outbox and on one in-memory bus):
///  1. The saga faults on its FIRST step: <c>context.Init&lt;DebitWalletCommand&gt;(new { ... })</c> throws
///     "ArgumentException: No default constructor available for message type" in MassTransit 8.3.0, because the
///     contracts in SharedKernel/Contracts/Purchase are positional records. The instance enters Initial and never
///     reaches ProcessingPayment, no saga row is stored, nothing is sent to debit-wallet, and the request client
///     receives Fault&lt;SubmitPurchaseCommand&gt;.
///  2. PurchaseChapterCommandHandler catches that in <c>catch (Exception)</c> and calls SaveFailedTransactionAsync,
///     which inserts the already-committed transaction again: DbUpdateException (PK_Transactions). The controller
///     does not catch it, so the client gets an unhandled 500.
///  3. Result: the Purchase transaction stays Pending forever, the wallet is NOT debited, the chapter is NOT unlocked.
///     Because the debit never runs, "insufficient balance" ends the same way; the saga's success, refund and failure
///     transitions are unreachable today.
///
/// THESE TESTS ARE EXPECTED TO FAIL WHEN THE BUG IS FIXED (PLAN.md: "Fix PurchaseStateMachine contracts ...").
/// That task must replace them with tests of the fixed behaviour; do not "fix" them to keep them green.
/// </summary>
public abstract class PurchaseKnownBugScenarios
{
    protected abstract Task<TestWorld> WorldAsync();

    private const string FirstStepFault = "No default constructor available for message type (Parameter 'TMessage')";

    private static async Task<(DbUpdateException Error, List<PaymentAPI.Entities.Transaction> Txs)> PurchaseThroughControllerAsync(
        TestWorld world, int user, Guid chapter)
    {
        using var scope = world.PaymentScope();
        var controller = Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString());

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => controller.PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = chapter }));
        return (error, await world.TransactionsAsync(user));
    }

    private static async Task AssertFirstStepFaultLoggedAsync(TestWorld world)
    {
        Assert.True(await TestWorld.EventuallyAsync(() => Task.FromResult(world.Messages.Snapshot().Contains("Fault`1"))),
            "Fault<SubmitPurchaseCommand> was never consumed. Messages: " + string.Join(", ", world.Messages.Snapshot()));
        await Task.Delay(500); // let anything that would follow the fault arrive
        Assert.Equal(new[] { nameof(SubmitPurchaseCommand), "Fault`1" }, world.Messages.Snapshot());
        // The instance enters Initial, then the first activity throws: it never reaches ProcessingPayment.
        Assert.Equal(new[] { "(none)->Initial" }, world.States.All());
    }

    protected async Task Purchase_with_enough_balance_crashes_with_500_and_charges_nothing()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);

        var (error, txs) = await PurchaseThroughControllerAsync(world, user, chapter);

        var sql = Assert.IsType<SqlException>(error.InnerException);
        Assert.Contains("Violation of PRIMARY KEY constraint 'PK_Transactions'", sql.Message);

        var tx = Assert.Single(txs);
        Assert.Equal(TransactionType.Purchase, tx.Type);
        Assert.Equal(TransactionStatus.Pending, tx.Status); // never moves on
        Assert.Equal(120m, tx.Amount);
        Assert.Equal("Dâu", tx.CurrencyType);
        Assert.Equal(chapter, tx.ChapterId);
        Assert.Null(tx.Note);
        Assert.Contains(tx.Id.ToString(), sql.Message); // the duplicate key is this very transaction

        Assert.Equal(500m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user)); // only the seed credit
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Null(await world.PaymentPurchaseAsync(user, chapter));
        Assert.Empty(await world.SagasForAsync(user));
        await AssertFirstStepFaultLoggedAsync(world);
    }

    protected async Task Insufficient_balance_crashes_the_same_way_because_the_debit_is_never_reached()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 50m);

        var (_, txs) = await PurchaseThroughControllerAsync(world, user, chapter);

        Assert.Equal(TransactionStatus.Pending, Assert.Single(txs).Status);
        Assert.Equal(50m, await world.BalanceAsync(user));
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
        Assert.Empty(await world.SagasForAsync(user));
        await AssertFirstStepFaultLoggedAsync(world);
    }

    protected async Task Saga_faults_on_its_first_step_and_stores_no_instance()
    {
        var world = await WorldAsync();
        var user = TestWorld.NewUserId();
        var correlationId = Guid.NewGuid();
        using var scope = world.PaymentScope();
        var client = scope.ServiceProvider.GetRequiredService<IRequestClient<SubmitPurchaseCommand>>();

        var fault = await Assert.ThrowsAsync<RequestFaultException>(() =>
            client.GetResponse<PurchaseCompletedEvent, PurchaseFailedEvent>(
                new SubmitPurchaseCommand(correlationId, user, Guid.NewGuid(), Guid.NewGuid(), 120m, Guid.NewGuid())));

        var chain = new List<string>();
        for (var e = fault.Fault!.Exceptions.FirstOrDefault(); e != null; e = e.InnerException)
            chain.Add($"{e.ExceptionType}: {e.Message}");
        Assert.Equal(new[]
        {
            "MassTransit.EventExecutionException: The SubmitPurchaseEvent<SubmitPurchaseCommand> (Event) execution faulted",
            "MassTransit.EventExecutionException: The SubmitPurchaseEvent<SubmitPurchaseCommand> (Event) execution faulted",
            $"System.ArgumentException: {FirstStepFault}",
        }, chain);

        Assert.Null(await world.SagaAsync(correlationId)); // the faulted instance is not persisted
        Assert.Equal(new[] { "(none)->Initial" }, world.States.For(correlationId));
        Assert.Null(await world.BalanceAsync(user));
    }
}

/// <summary>The known bug on three separate buses over RabbitMQ with the EF Outbox: what runs today.</summary>
[Collection(T37Collection.Name)]
public class PurchaseKnownBugTests_RabbitMq : PurchaseKnownBugScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseKnownBugTests_RabbitMq(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.RabbitMqAsync();

    [RabbitMqFact] public Task KnownBug_Purchase_with_enough_balance_crashes_with_500_and_charges_nothing() => Purchase_with_enough_balance_crashes_with_500_and_charges_nothing();
    [RabbitMqFact] public Task KnownBug_Insufficient_balance_crashes_the_same_way() => Insufficient_balance_crashes_the_same_way_because_the_debit_is_never_reached();
    [RabbitMqFact] public Task KnownBug_Saga_faults_on_its_first_step_and_stores_no_instance() => Saga_faults_on_its_first_step_and_stores_no_instance();
}

/// <summary>The known bug on one in-memory bus without outbox (the T20/T21 target): same outcome.</summary>
[Collection(T37Collection.Name)]
public class PurchaseKnownBugTests_InMemory : PurchaseKnownBugScenarios
{
    private readonly T37Fixture _fixture;
    public PurchaseKnownBugTests_InMemory(T37Fixture fixture) => _fixture = fixture;
    protected override Task<TestWorld> WorldAsync() => _fixture.InMemoryAsync();

    [Fact] public Task KnownBug_Purchase_with_enough_balance_crashes_with_500_and_charges_nothing() => Purchase_with_enough_balance_crashes_with_500_and_charges_nothing();
    [Fact] public Task KnownBug_Insufficient_balance_crashes_the_same_way() => Insufficient_balance_crashes_the_same_way_because_the_debit_is_never_reached();
    [Fact] public Task KnownBug_Saga_faults_on_its_first_step_and_stores_no_instance() => Saga_faults_on_its_first_step_and_stores_no_instance();
}
