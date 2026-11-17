using System.Globalization;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Features.Payments.Commands;
using PaymentAPI.DTOs;
using PaymentAPI.Interfaces;
using PaymentAPI.Services;
using PaymentAPI.Settings;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// T37 characterization: POST /payments/sepay-webhook (SepayWebhookAuthenticator + ProcessSePayWebhookCommand) and
/// the wallet credit it triggers through the real WalletGrpcService. Expected values were observed by running the
/// current code against throwaway databases with a fake webhook key; they describe what the code does today.
/// Notes are built with string interpolation, so they depend on the server culture; these tests pin them under
/// the invariant culture.
/// </summary>
[Collection(T37Collection.Name)]
public class SePayWebhookTests
{
    private const string Key = "t37-sepay-key"; // fake, see TestWorld.DefaultBank
    private const string WalletAddCoin = "/wallet.WalletService/AddCoin";
    private static long _nextSePayId = Random.Shared.NextInt64(1_000_000, 9_000_000);

    private readonly T37Fixture _fixture;

    public SePayWebhookTests(T37Fixture fixture)
    {
        _fixture = fixture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static SePayWebhookDto Transfer(string? content, decimal amount, string transferType = "in") => new()
    {
        Id = Interlocked.Increment(ref _nextSePayId),
        Gateway = "Vietcombank",
        TransactionDate = "2026-11-17 10:00:00",
        AccountNumber = "0123456789",
        Content = content,
        TransferType = transferType,
        TransferAmount = amount,
        ReferenceCode = "FT-T37",
    };

    private static async Task<DepositResponseDto> CreateDepositAsync(TestWorld world, int user, decimal amount)
    {
        using var scope = world.PaymentScope();
        var response = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new CreateDepositCommand { UserId = user, Amount = amount });
        return response.Data!;
    }

    private static async Task<ApiResponse<bool>> SendAsync(TestWorld world, SePayWebhookDto dto)
    {
        using var scope = world.PaymentScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ProcessSePayWebhookCommand { Request = dto });
    }

    // ---------------- authentication (bad signature) ----------------

    [Theory]
    [InlineData(null, Key, false)]                    // no Authorization header
    [InlineData("", Key, false)]
    [InlineData("Apikey wrong-key", Key, false)]
    [InlineData("Apikey t37-sepay-key-extra", Key, false)] // whole-value match, not prefix
    [InlineData("Apikey t37-sepay-key", Key, true)]
    [InlineData("apikey t37-sepay-key", Key, true)]   // scheme is case-insensitive
    [InlineData("Bearer t37-sepay-key", Key, true)]
    [InlineData("t37-sepay-key", Key, true)]          // bare token accepted
    [InlineData("  Apikey   t37-sepay-key  ", Key, true)]
    [InlineData("Apikey T37-SEPAY-KEY", Key, false)]  // the token itself is case-sensitive
    [InlineData("Apikey t37-sepay-key", "", false)]   // no key configured: fail closed
    [InlineData("Apikey ", " ", false)]
    public void Authenticator_accepts_only_the_configured_key(string? header, string configuredKey, bool expected)
    {
        Assert.Equal(expected, SepayWebhookAuthenticator.IsAuthorized(header, configuredKey));
    }

    [Fact]
    public async Task Webhook_with_a_wrong_key_returns_401_and_credits_nothing()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        using var scope = world.PaymentScope();
        var addCoinCallsBefore = world.Grpc.Calls.Count(c => c == WalletAddCoin);

        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), authorization: "Apikey wrong-key")
            .SePayWebhook(Transfer($"NAP {deposit.TransactionCode}", 50000m), Options.Create(TestWorld.DefaultBank));

        Assert.Equal(401, Controllers.StatusOf(result));
        var message = ((ObjectResult)result).Value!.GetType().GetProperty("Message")!.GetValue(((ObjectResult)result).Value);
        Assert.Equal("Invalid SePay webhook token.", message);
        Assert.Equal(TransactionStatus.Pending, Assert.Single(await world.TransactionsAsync(user)).Status);
        Assert.Null(await world.BalanceAsync(user));
        Assert.Equal(addCoinCallsBefore, world.Grpc.Calls.Count(c => c == WalletAddCoin));
    }

    [Fact]
    public async Task Webhook_with_the_right_key_always_answers_200_and_carries_the_verdict_in_the_body()
    {
        var world = await _fixture.InMemoryAsync();
        using var scope = world.PaymentScope();

        // A transfer whose code matches nothing: the handler says 404, the HTTP status stays 200.
        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), authorization: $"Apikey {Key}")
            .SePayWebhook(Transfer("NAP UP000000000000000000", 50000m), Options.Create(TestWorld.DefaultBank));

        Assert.Equal(200, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<bool>>(((ObjectResult)result).Value);
        Assert.True(body.Success);
        Assert.False(body.Data);
        Assert.Equal(404, body.StatusCode);
        Assert.Equal("Transaction not found.", body.Message);
    }

    // ---------------- requests that are ignored ----------------

    [Fact]
    public async Task Outgoing_transfer_is_ignored()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);

        var response = await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m, transferType: "out"));

        Assert.True(response.Success);
        Assert.False(response.Data);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Not an incoming transfer.", response.Message);
        Assert.Equal(TransactionStatus.Pending, Assert.Single(await world.TransactionsAsync(user)).Status);
        Assert.Null(await world.BalanceAsync(user));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("chuyen tien khong ma")]
    [InlineData("UP12345")]                     // too short
    public async Task Transfer_without_a_deposit_code_returns_404_in_the_body(string? content)
    {
        var world = await _fixture.InMemoryAsync();

        var response = await SendAsync(world, Transfer(content, 50000m));

        Assert.True(response.Success);
        Assert.False(response.Data);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Transaction code not found in content.", response.Message);
    }

    [Fact]
    public async Task Transfer_with_a_non_positive_amount_returns_400_and_leaves_the_deposit_pending()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);

        var response = await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 0m));

        Assert.False(response.Data);
        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Transfer amount must be positive.", response.Message);
        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(TransactionStatus.Pending, tx.Status);
        Assert.Null(tx.Note);
        Assert.Null(await world.BalanceAsync(user));
    }

    // ---------------- successful deposit ----------------

    [Fact]
    public async Task Exact_payment_completes_the_deposit_and_credits_the_wallet_once()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"MBVCB.123 NAP {deposit.TransactionCode} FT-T37", 50000m);

        var response = await SendAsync(world, dto);

        Assert.True(response.Success);
        Assert.True(response.Data);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Topup transaction completed via SePay Webhook.", response.Message);

        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(deposit.TransactionId, tx.Id);
        Assert.Equal(TransactionStatus.Completed, tx.Status);
        Assert.Equal(50000m, tx.Amount);
        Assert.Equal(dto.Id.ToString(), tx.ExternalTransactionId);
        Assert.Equal($"SEPAY id={dto.Id}; ref=FT-T37; gateway=Vietcombank; date=2026-11-17 10:00:00; received=50000", tx.Note);

        Assert.Equal(50000m, await world.BalanceAsync(user));
        var entry = Assert.Single(await world.LedgerAsync(user));
        Assert.Equal((TransactionType.ManualTopUp, 50000m, (Guid?)deposit.TransactionId), entry);
    }

    [Fact]
    public async Task Amount_mismatch_credits_what_was_received_and_flags_the_note()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"NAP {deposit.TransactionCode}", 60000m);

        var response = await SendAsync(world, dto);

        Assert.True(response.Data);
        Assert.Equal("Topup transaction completed via SePay Webhook.", response.Message);
        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(TransactionStatus.Completed, tx.Status);
        Assert.Equal(60000m, tx.Amount); // overwritten with the amount actually received
        Assert.Equal(
            $"SEPAY id={dto.Id}; ref=FT-T37; gateway=Vietcombank; date=2026-11-17 10:00:00; received=60000; requested=50000.00 (MISMATCH)",
            tx.Note);
        Assert.Equal(60000m, await world.BalanceAsync(user));
    }

    [Fact]
    public async Task Underpayment_is_credited_as_received_too()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);

        var response = await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 10000m));

        Assert.True(response.Data);
        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(10000m, tx.Amount);
        Assert.EndsWith("received=10000; requested=50000.00 (MISMATCH)", tx.Note);
        Assert.Equal(10000m, await world.BalanceAsync(user));
    }

    [Fact]
    public async Task Duplicate_delivery_of_the_same_SePay_id_is_idempotent()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"NAP {deposit.TransactionCode}", 50000m);

        var first = await SendAsync(world, dto);
        var second = await SendAsync(world, dto);

        Assert.True(first.Data);
        Assert.True(second.Success);
        Assert.True(second.Data);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal("Webhook already processed.", second.Message);
        Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(50000m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user));
    }

    /// <summary>
    /// KNOWN BUG (observed by running, T37): the handler means to record a second transfer on an already used deposit
    /// code as a NEW top-up ("Create a new transaction for this extra payment"), but that row reuses the same
    /// TransactionCode and the unique index IX_Transactions_TransactionCode rejects it. AddTransactionAsync is outside
    /// the DbUpdateException guard, so the exception escapes: the webhook answers 500, the money is not credited, and
    /// every SePay retry fails the same way. EXPECTED TO FAIL WHEN THE BUG IS FIXED; replace it then, do not patch it.
    /// </summary>
    [Fact]
    public async Task KnownBug_Second_transfer_with_the_same_code_crashes_on_the_unique_code_index_and_is_not_credited()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
        var extra = Transfer($"NAP {deposit.TransactionCode}", 30000m);

        var error = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => SendAsync(world, extra));
        var retry = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => SendAsync(world, extra));

        foreach (var e in new[] { error, retry })
        {
            Assert.Contains(
                $"Cannot insert duplicate key row in object 'dbo.Transactions' with unique index 'IX_Transactions_TransactionCode'. The duplicate key value is ({deposit.TransactionCode}).",
                e.InnerException!.Message);
        }
        var tx = Assert.Single(await world.TransactionsAsync(user)); // the first deposit only, unchanged
        Assert.Equal(TransactionStatus.Completed, tx.Status);
        Assert.Equal(50000m, tx.Amount);
        Assert.Equal(50000m, await world.BalanceAsync(user)); // the 30000 was never credited
        Assert.Single(await world.LedgerAsync(user));
    }

    [Fact]
    public async Task KnownBug_Second_transfer_with_the_same_code_through_the_controller_escapes_as_an_exception()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
        using var scope = world.PaymentScope();
        var controller = Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), authorization: $"Apikey {Key}");

        // The controller only wraps the handler's answer in 200; an exception is not caught, so ASP.NET answers 500.
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            controller.SePayWebhook(Transfer($"NAP {deposit.TransactionCode}", 30000m), Options.Create(TestWorld.DefaultBank)));
        Assert.Equal(50000m, await world.BalanceAsync(user));
    }

    [Fact]
    public async Task Wallet_unavailable_leaves_the_deposit_pending_and_a_redelivery_completes_it_once()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"NAP {deposit.TransactionCode}", 50000m);

        world.Grpc.Unavailable.Add(WalletAddCoin);
        var failed = await SendAsync(world, dto);

        Assert.False(failed.Success);
        Assert.Equal(503, failed.StatusCode);
        Assert.Equal("Could not credit the wallet. Transaction stays pending; retry later.", failed.Message);
        var pending = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(TransactionStatus.Pending, pending.Status);
        Assert.Equal(dto.Id.ToString(), pending.ExternalTransactionId); // already claimed by this SePay id
        Assert.StartsWith($"SEPAY id={dto.Id};", pending.Note);
        Assert.Null(await world.BalanceAsync(user));

        world.Grpc.Unavailable.Clear();
        var retried = await SendAsync(world, dto);

        Assert.True(retried.Data);
        Assert.Equal("Topup transaction completed via SePay Webhook.", retried.Message);
        Assert.Equal(TransactionStatus.Completed, Assert.Single(await world.TransactionsAsync(user)).Status);
        Assert.Equal(50000m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user));
    }

    [Fact]
    public async Task Coin_rate_multiplies_the_wallet_credit_but_not_the_transaction_amount()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        using var scope = world.PaymentScope();
        var sp = scope.ServiceProvider;
        var handler = new ProcessSePayWebhookCommandHandler(
            sp.GetRequiredService<IPaymentRepository>(),
            sp.GetRequiredService<ChapterAPI.Protos.ChapterGrpc.ChapterGrpcClient>(),
            sp.GetRequiredService<WalletAPI.Protos.WalletService.WalletServiceClient>(),
            sp.GetRequiredService<IRequestClient<SubmitPurchaseCommand>>(),
            Options.Create(new TopUpSettings { CoinRate = 2m }),
            Options.Create(TestWorld.DefaultBank));

        var response = await handler.Handle(
            new ProcessSePayWebhookCommand { Request = Transfer($"NAP {deposit.TransactionCode}", 50000m) }, default);

        Assert.True(response.Data);
        Assert.Equal(50000m, Assert.Single(await world.TransactionsAsync(user)).Amount);
        Assert.Equal(100000m, await world.BalanceAsync(user));
    }
}
