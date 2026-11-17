using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Contracts.Modules;
using WalletAPI.Application.Features.Wallet.Commands;

namespace WalletAPI.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC call <c>WalletService.AddCoin</c> (same command, same validation and same
/// answer as <c>WalletGrpcService.AddCoin</c>: a user id or amount of 0 or less is rejected, <see cref="Guid.Empty"/>
/// is accepted, and <c>Success</c> is true when the credit is done or was already processed). Any failure other
/// than cancellation is wrapped in <see cref="ModuleCallException"/>.
/// The call runs in its own DI scope, like a gRPC request did, so the credit uses its own <c>WalletDbContext</c>
/// and never joins a transaction or change tracker of the caller.
/// </summary>
public sealed class PaymentWalletGateway : IPaymentWalletGateway
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentWalletGateway(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<WalletCreditResult> AddCoinAsync(
        int userId,
        decimal amount,
        Guid referenceId,
        string description,
        string creditType,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || amount <= 0)
        {
            throw new ModuleCallException("Invalid wallet request.");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new AddCoinCommand
            {
                UserId = userId,
                Amount = amount,
                ReferenceId = referenceId,
                CreditType = creditType,
                Description = description
            }, cancellationToken);

            return new WalletCreditResult(
                Success: true,
                Message: result.AlreadyProcessed ? "Already processed." : "Coins added successfully.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("WalletAPI could not credit the wallet.", ex);
        }
    }
}
