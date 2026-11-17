using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;
using WalletAPI.DTOs;
using WalletAPI.Interfaces;

namespace WalletAPI.Application.Features.Wallet.Commands
{
    /// <summary>
    /// Refunds a chapter purchase outside the saga (T23 purchase recovery job). Same credit as
    /// <c>RefundWalletConsumer</c>: <see cref="TransactionType.Refund"/>, reference id
    /// <see cref="PurchaseReferenceIds.RefundOf"/> of the transaction, same description. So a refund already made by
    /// the saga is not applied again (<see cref="WalletCreditResultDto.AlreadyProcessed"/> true), and the other way round.
    /// </summary>
    public class RefundPurchaseCommand : IRequest<WalletCreditResultDto>
    {
        public int UserId { get; set; }
        public decimal Amount { get; set; }
        public Guid TransactionId { get; set; }
    }

    public class RefundPurchaseCommandHandler : IRequestHandler<RefundPurchaseCommand, WalletCreditResultDto>
    {
        private readonly IWalletRepository _repository;

        public RefundPurchaseCommandHandler(IWalletRepository repository)
        {
            _repository = repository;
        }

        public Task<WalletCreditResultDto> Handle(RefundPurchaseCommand request, CancellationToken cancellationToken)
        {
            return _repository.CreditAsync(
                request.UserId,
                request.Amount,
                PurchaseReferenceIds.RefundOf(request.TransactionId),
                TransactionType.Refund,
                $"Hoàn tiền do lỗi mở chapter, Transaction: {request.TransactionId}");
        }
    }
}
