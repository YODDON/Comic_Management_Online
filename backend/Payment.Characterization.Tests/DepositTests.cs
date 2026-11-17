using System.Text.RegularExpressions;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Features.Payments.Commands;
using PaymentAPI.DTOs;
using PaymentAPI.Interfaces;
using PaymentAPI.Settings;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// T37 characterization: POST /payments/deposit (CreateDepositCommand). Expected values were observed by running
/// the current code against a throwaway database; they describe what the code does, not what it should do.
/// </summary>
[Collection(T37Collection.Name)]
public class DepositTests
{
    private readonly T37Fixture _fixture;
    public DepositTests(T37Fixture fixture) => _fixture = fixture;

    private static CreateDepositCommandHandler HandlerWith(IServiceProvider sp, BankSettings bank) => new(
        sp.GetRequiredService<IPaymentRepository>(),
        sp.GetRequiredService<ChapterAPI.Protos.ChapterGrpc.ChapterGrpcClient>(),
        sp.GetRequiredService<WalletAPI.Protos.WalletService.WalletServiceClient>(),
        sp.GetRequiredService<IRequestClient<SubmitPurchaseCommand>>(),
        Options.Create(new TopUpSettings()),
        Options.Create(bank));

    [Fact]
    public async Task Successful_deposit_creates_a_pending_VietQR_top_up_and_returns_the_QR_url()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        using var scope = world.PaymentScope();

        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString())
            .CreateDeposit(new DepositRequestDto { Amount = 50000m });

        Assert.Equal(200, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<DepositResponseDto>>(((ObjectResult)result).Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        Assert.Matches(new Regex(@"^UP\d{18}$"), body.Data!.TransactionCode);
        Assert.Equal(50000m, body.Data.Amount);
        Assert.Equal(
            $"https://img.vietqr.io/image/VCB-0123456789-compact2.png?amount=50000&addInfo={body.Data.TransactionCode}&accountName=TEST%20ACCOUNT",
            body.Data.QrUrl);

        var tx = Assert.Single(await world.TransactionsAsync(user));
        Assert.Equal(body.Data.TransactionId, tx.Id);
        Assert.Equal(TransactionType.ManualTopUp, tx.Type);
        Assert.Equal(TransactionStatus.Pending, tx.Status);
        Assert.Equal(50000m, tx.Amount);
        Assert.Equal("VND", tx.CurrencyType);
        Assert.Equal("VIETQR", tx.PaymentMethod);
        Assert.Equal(body.Data.TransactionCode, tx.TransactionCode);
        Assert.Null(tx.Note);
        Assert.Null(tx.ExternalTransactionId);
        Assert.Null(tx.ChapterId);

        // Creating a deposit never touches the wallet.
        Assert.Null(await world.BalanceAsync(user));
    }

    [Fact]
    public async Task Deposit_without_account_name_leaves_accountName_out_of_the_QR_url()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        using var scope = world.PaymentScope();
        var bank = TestWorld.DefaultBank;
        bank.AccountName = "";

        var response = await HandlerWith(scope.ServiceProvider, bank).Handle(new CreateDepositCommand { UserId = user, Amount = 20000m }, default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(
            $"https://img.vietqr.io/image/VCB-0123456789-compact2.png?amount=20000&addInfo={response.Data!.TransactionCode}",
            response.Data.QrUrl);
    }

    [Fact]
    public async Task Deposit_when_the_bank_account_is_not_configured_returns_503_and_writes_nothing()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        using var scope = world.PaymentScope();

        var response = await HandlerWith(scope.ServiceProvider, new BankSettings { SepayApiKey = "t37-sepay-key" })
            .Handle(new CreateDepositCommand { UserId = user, Amount = 50000m }, default);

        Assert.False(response.Success);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("Deposits are unavailable: the receiving bank account is not configured.", response.Message);
        Assert.Empty(await world.TransactionsAsync(user));
    }

    [Theory]
    [InlineData("0", 400, "Amount must be greater than zero.")]
    [InlineData("-5000", 400, "Amount must be greater than zero.")]
    [InlineData("10000.5", 422, "Amount must be a whole number of VND.")]
    public async Task Invalid_deposit_amounts_are_rejected_and_write_nothing(string amount, int status, string message)
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        using var scope = world.PaymentScope();

        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString())
            .CreateDeposit(new DepositRequestDto { Amount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture) });

        Assert.Equal(status, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<DepositResponseDto>>(((ObjectResult)result).Value);
        Assert.False(body.Success);
        Assert.Equal(message, body.Message);
        Assert.Empty(await world.TransactionsAsync(user));
    }

    [Fact]
    public async Task Deposit_with_a_non_numeric_user_claim_returns_401()
    {
        var world = await _fixture.InMemoryAsync();
        using var scope = world.PaymentScope();

        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), "not-a-number")
            .CreateDeposit(new DepositRequestDto { Amount = 50000m });

        Assert.Equal(401, Controllers.StatusOf(result));
    }
}
