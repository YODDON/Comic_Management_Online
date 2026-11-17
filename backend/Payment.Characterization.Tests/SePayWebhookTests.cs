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

    // ---------------- extra transfers on an already used deposit code (T40) ----------------

    private static async Task<IActionResult> ThroughControllerAsync(TestWorld world, SePayWebhookDto dto)
    {
        using var scope = world.PaymentScope();
        return await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), authorization: $"Apikey {Key}")
            .SePayWebhook(dto, Options.Create(TestWorld.DefaultBank));
    }

    private static ProcessSePayWebhookCommandHandler HandlerWith(IServiceProvider sp, IPaymentRepository repository) => new(
        repository,
        sp.GetRequiredService<SharedKernel.Contracts.Modules.IPaymentChapterGateway>(),
        sp.GetRequiredService<SharedKernel.Contracts.Modules.IPaymentWalletGateway>(),
        sp.GetRequiredService<IRequestClient<SubmitPurchaseCommand>>(),
        Options.Create(new TopUpSettings { CoinRate = 1m }),
        Options.Create(TestWorld.DefaultBank));

    /// <summary>
    /// T40 (was KnownBug_Second_transfer_with_the_same_code_crashes_...): a second transfer on an already used deposit
    /// code with a NEW SePay id is credited to the same user's wallet as a separate deposit. The extra row is keyed
    /// "{code}-{sePayId}" because TransactionCode is unique per row; the original row is untouched.
    /// </summary>
    [Fact]
    public async Task Second_transfer_with_the_same_code_and_a_new_SePay_id_is_credited_as_a_separate_deposit()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var first = Transfer($"NAP {deposit.TransactionCode}", 50000m);
        var extra = Transfer($"NAP {deposit.TransactionCode}", 30000m);

        await SendAsync(world, first);
        var response = await SendAsync(world, extra);

        Assert.True(response.Data);
        Assert.Equal("Topup transaction completed via SePay Webhook.", response.Message);
        var rows = await world.TransactionsAsync(user);
        Assert.Equal(2, rows.Count);
        var original = rows.Single(t => t.TransactionCode == deposit.TransactionCode);
        var added = rows.Single(t => t.TransactionCode != deposit.TransactionCode);
        Assert.Equal(TransactionStatus.Completed, original.Status);
        Assert.Equal(50000m, original.Amount);
        Assert.Equal(first.Id.ToString(), original.ExternalTransactionId);
        Assert.Equal($"{deposit.TransactionCode}-{extra.Id}", added.TransactionCode);
        Assert.Equal(TransactionStatus.Completed, added.Status);
        Assert.Equal(TransactionType.ManualTopUp, added.Type);
        Assert.Equal(30000m, added.Amount);
        Assert.Equal(extra.Id.ToString(), added.ExternalTransactionId);
        Assert.StartsWith($"SEPAY id={extra.Id};", added.Note);
        Assert.EndsWith("received=30000", added.Note); // an extra payment has no requested amount to mismatch
        Assert.Equal(80000m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(2, ledger.Select(l => l.ReferenceId).Distinct().Count());
    }

    [Fact]
    public async Task Second_transfer_with_the_same_code_through_the_controller_answers_200_and_is_credited()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));

        var result = await ThroughControllerAsync(world, Transfer($"NAP {deposit.TransactionCode}", 30000m));

        Assert.Equal(200, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<bool>>(((ObjectResult)result).Value);
        Assert.True(body.Success);
        Assert.True(body.Data);
        Assert.Equal(80000m, await world.BalanceAsync(user));
    }

    [Fact]
    public async Task Replaying_the_extra_transfer_with_the_same_SePay_id_credits_it_once()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
        var extra = Transfer($"NAP {deposit.TransactionCode}", 30000m);

        await SendAsync(world, extra);
        var replay = await SendAsync(world, extra);
        var replayAgain = await SendAsync(world, extra);

        foreach (var r in new[] { replay, replayAgain })
        {
            Assert.True(r.Data);
            Assert.Equal("Webhook already processed.", r.Message);
        }
        Assert.Equal(2, (await world.TransactionsAsync(user)).Count);
        Assert.Equal(80000m, await world.BalanceAsync(user));
        Assert.Equal(2, (await world.LedgerAsync(user)).Count);
    }

    [Fact]
    public async Task Three_transfers_on_one_code_are_three_deposits_and_each_replay_is_ignored()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var transfers = new[]
        {
            Transfer($"NAP {deposit.TransactionCode}", 50000m),
            Transfer($"NAP {deposit.TransactionCode}", 30000m),
            Transfer($"NAP {deposit.TransactionCode}", 20000m),
        };

        foreach (var t in transfers) Assert.True((await SendAsync(world, t)).Data);
        foreach (var t in transfers) Assert.Equal("Webhook already processed.", (await SendAsync(world, t)).Message);

        var rows = await world.TransactionsAsync(user);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(TransactionStatus.Completed, r.Status));
        Assert.Equal(3, rows.Select(r => r.TransactionCode).Distinct().Count());
        Assert.Equal(1, rows.Count(r => r.TransactionCode == deposit.TransactionCode));
        Assert.Equal(100000m, await world.BalanceAsync(user));
        var ledger = await world.LedgerAsync(user);
        Assert.Equal(3, ledger.Count);
        Assert.Equal(3, ledger.Select(l => l.ReferenceId).Distinct().Count());
    }

    [Fact]
    public async Task Extra_transfer_while_the_wallet_is_down_answers_http_503_and_a_retry_credits_it_once()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
        var extra = Transfer($"NAP {deposit.TransactionCode}", 30000m);

        world.Grpc.Unavailable.Add(WalletAddCoin);
        var failed = await ThroughControllerAsync(world, extra);

        Assert.Equal(503, Controllers.StatusOf(failed)); // a real HTTP status, so SePay retries
        var body = Assert.IsType<ApiResponse<bool>>(((ObjectResult)failed).Value);
        Assert.False(body.Success);
        Assert.Equal("Could not credit the wallet. Transaction stays pending; retry later.", body.Message);
        var pending = Assert.Single(await world.TransactionsAsync(user), t => t.TransactionCode != deposit.TransactionCode);
        Assert.Equal(TransactionStatus.Pending, pending.Status);
        Assert.Equal(extra.Id.ToString(), pending.ExternalTransactionId);
        Assert.Equal(50000m, await world.BalanceAsync(user));

        world.Grpc.Unavailable.Clear();
        var retried = await ThroughControllerAsync(world, extra);

        Assert.Equal(200, Controllers.StatusOf(retried));
        Assert.True(((ApiResponse<bool>)((ObjectResult)retried).Value!).Data);
        var rows = await world.TransactionsAsync(user);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TransactionStatus.Completed, r.Status));
        Assert.Equal(80000m, await world.BalanceAsync(user));
        Assert.Equal(2, (await world.LedgerAsync(user)).Count);
    }

    /// <summary>
    /// The same SePay id delivered twice at the same time. Whichever way the race goes (claimed, "already processed",
    /// or a 503 because the wallet's serializable transaction lost), it ends with ONE row per id and ONE credit once the
    /// 503 is retried. Run for both the take-over of the original row and the extra-transfer row.
    /// </summary>
    [Theory]
    [InlineData(false)] // first transfer: both deliveries try to take over the pending deposit
    [InlineData(true)]  // extra transfer: both deliveries try to insert the extra row
    public async Task Concurrent_deliveries_of_the_same_SePay_id_credit_it_once(bool extraTransfer)
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        decimal expected = 50000m;
        if (extraTransfer)
        {
            await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
            expected = 80000m;
        }
        var dto = Transfer($"NAP {deposit.TransactionCode}", extraTransfer ? 30000m : 50000m);

        var results = await Task.WhenAll(ThroughControllerAsync(world, dto), ThroughControllerAsync(world, dto));
        foreach (var result in results)
        {
            var status = Controllers.StatusOf(result);
            Assert.True(status is 200 or 503, $"unexpected HTTP {status}");
        }

        // SePay retries a 503; a redelivery after the race must settle everything.
        var settled = await ThroughControllerAsync(world, dto);
        Assert.Equal(200, Controllers.StatusOf(settled));

        var rows = await world.TransactionsAsync(user);
        Assert.Equal(extraTransfer ? 2 : 1, rows.Count);
        Assert.All(rows, r => Assert.Equal(TransactionStatus.Completed, r.Status));
        Assert.Single(rows, r => r.ExternalTransactionId == dto.Id.ToString());
        Assert.Equal(expected, await world.BalanceAsync(user));
        Assert.Equal(extraTransfer ? 2 : 1, (await world.LedgerAsync(user)).Count);
    }

    /// <summary>
    /// The insert of the extra row fails and no row exists for this SePay id: nothing is recorded, so the failure is
    /// not swallowed (the controller does not catch it: HTTP 500) and SePay retries; the retry then credits once.
    /// </summary>
    [Fact]
    public async Task Extra_transfer_whose_insert_fails_with_nothing_recorded_escapes_and_a_retry_credits_it()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        await SendAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));
        var extra = Transfer($"NAP {deposit.TransactionCode}", 30000m);
        var failInsert = true;

        using (var scope = world.PaymentScope())
        {
            var repository = FaultInjectingProxy.Wrap(
                scope.ServiceProvider.GetRequiredService<IPaymentRepository>(),
                (method, _) => failInsert && method.Name == nameof(IPaymentRepository.AddTransactionAsync)
                    ? new Microsoft.EntityFrameworkCore.DbUpdateException("injected: insert failed", new InvalidOperationException("injected"))
                    : null);
            var handler = HandlerWith(scope.ServiceProvider, repository);

            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
                handler.Handle(new ProcessSePayWebhookCommand { Request = extra }, default));
        }

        Assert.Single(await world.TransactionsAsync(user)); // nothing recorded for the extra transfer
        Assert.Equal(50000m, await world.BalanceAsync(user));

        failInsert = false;
        var retried = await SendAsync(world, extra);

        Assert.True(retried.Data);
        Assert.Equal(2, (await world.TransactionsAsync(user)).Count);
        Assert.Equal(80000m, await world.BalanceAsync(user));
    }

    /// <summary>
    /// The transfer is recorded and the wallet is credited, then marking it Completed fails (any exception, not only a
    /// gRPC one): the handler answers 503 instead of an unhandled 500; the retry finds the row Pending, credits again
    /// with the same reference id (the wallet ignores it) and completes it. Credited once.
    /// </summary>
    [Fact]
    public async Task Failure_after_the_credit_answers_503_and_the_retry_completes_without_a_second_credit()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"NAP {deposit.TransactionCode}", 50000m);
        var updates = 0;

        using (var scope = world.PaymentScope())
        {
            var repository = FaultInjectingProxy.Wrap(
                scope.ServiceProvider.GetRequiredService<IPaymentRepository>(),
                (method, _) => method.Name == nameof(IPaymentRepository.UpdateTransactionAsync) && ++updates == 2
                    ? new InvalidOperationException("injected: completing the transaction failed")
                    : null);
            var response = await HandlerWith(scope.ServiceProvider, repository)
                .Handle(new ProcessSePayWebhookCommand { Request = dto }, default);

            Assert.False(response.Success);
            Assert.Equal(503, response.StatusCode);
            Assert.Equal("Could not complete the top-up. Transaction stays pending; retry later.", response.Message);
        }

        Assert.Equal(TransactionStatus.Pending, Assert.Single(await world.TransactionsAsync(user)).Status);
        Assert.Equal(50000m, await world.BalanceAsync(user)); // already credited once

        var retried = await ThroughControllerAsync(world, dto);

        Assert.Equal(200, Controllers.StatusOf(retried));
        Assert.Equal(TransactionStatus.Completed, Assert.Single(await world.TransactionsAsync(user)).Status);
        Assert.Equal(50000m, await world.BalanceAsync(user));
        Assert.Single(await world.LedgerAsync(user));
    }

    /// <summary>
    /// What SePay receives on success: HTTP 200 and a body with a top-level "success": true (SePay's documented success
    /// marker). Serialised with the web defaults MVC uses (camelCase), the exact JSON is pinned.
    /// </summary>
    [Fact]
    public async Task Success_answer_is_http_200_with_a_top_level_success_true()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);

        var result = await ThroughControllerAsync(world, Transfer($"NAP {deposit.TransactionCode}", 50000m));

        Assert.Equal(200, Controllers.StatusOf(result));
        var json = System.Text.Json.JsonSerializer.Serialize(
            ((ObjectResult)result).Value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(
            "{\"success\":true,\"message\":\"Topup transaction completed via SePay Webhook.\",\"data\":true,\"statusCode\":200,\"errors\":[]}",
            json);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Wallet_unavailable_leaves_the_deposit_pending_and_a_redelivery_completes_it_once()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var deposit = await CreateDepositAsync(world, user, 50000m);
        var dto = Transfer($"NAP {deposit.TransactionCode}", 50000m);

        world.Grpc.Unavailable.Add(WalletAddCoin);
        var failedResult = await ThroughControllerAsync(world, dto);
        var failed = Assert.IsType<ApiResponse<bool>>(((ObjectResult)failedResult).Value);

        Assert.Equal(503, Controllers.StatusOf(failedResult)); // real HTTP 503 (T40): SePay retries
        Assert.False(failed.Success);
        Assert.Equal(503, failed.StatusCode);
        Assert.Equal("Could not credit the wallet. Transaction stays pending; retry later.", failed.Message);
        var pending = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(TransactionStatus.Pending, pending.Status);
        Assert.Equal(dto.Id.ToString(), pending.ExternalTransactionId); // already claimed by this SePay id
        Assert.StartsWith($"SEPAY id={dto.Id};", pending.Note);
        Assert.Null(await world.BalanceAsync(user));

        world.Grpc.Unavailable.Clear();
        var retriedResult = await ThroughControllerAsync(world, dto);
        var retried = Assert.IsType<ApiResponse<bool>>(((ObjectResult)retriedResult).Value);

        Assert.Equal(200, Controllers.StatusOf(retriedResult));
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
            sp.GetRequiredService<SharedKernel.Contracts.Modules.IPaymentChapterGateway>(),
            sp.GetRequiredService<SharedKernel.Contracts.Modules.IPaymentWalletGateway>(),
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
