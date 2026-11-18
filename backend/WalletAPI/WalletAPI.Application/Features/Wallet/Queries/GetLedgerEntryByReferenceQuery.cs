using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using WalletAPI.DTOs;
using WalletAPI.Interfaces;

namespace WalletAPI.Application.Features.Wallet.Queries
{
    /// <summary>
    /// The ledger entry with this reference id, or null. Read only. Used through <c>IPaymentWalletGateway</c> by the
    /// purchase recovery job (T23) to check whether a purchase was debited or refunded.
    /// </summary>
    public class GetLedgerEntryByReferenceQuery : IRequest<WalletLedgerEntryDto?>
    {
        public Guid ReferenceId { get; set; }
    }

    public class GetLedgerEntryByReferenceQueryHandler : IRequestHandler<GetLedgerEntryByReferenceQuery, WalletLedgerEntryDto?>
    {
        private readonly IWalletRepository _repository;

        public GetLedgerEntryByReferenceQueryHandler(IWalletRepository repository)
        {
            _repository = repository;
        }

        public Task<WalletLedgerEntryDto?> Handle(GetLedgerEntryByReferenceQuery request, CancellationToken cancellationToken)
        {
            return _repository.GetEntryByReferenceIdAsync(request.ReferenceId);
        }
    }
}
