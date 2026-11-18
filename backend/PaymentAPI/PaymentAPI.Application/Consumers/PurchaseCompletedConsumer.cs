using System;
using System.Threading.Tasks;
using MassTransit;
using PaymentAPI.Application.Services;
using PaymentAPI.Interfaces;
using SharedKernel.Contracts.Purchase;

namespace PaymentAPI.Application.Consumers
{
    public class PurchaseCompletedConsumer : IConsumer<PurchaseCompletedEvent>
    {
        private readonly IPaymentRepository _repository;

        public PurchaseCompletedConsumer(IPaymentRepository repository)
        {
            _repository = repository;
        }

        public async Task Consume(ConsumeContext<PurchaseCompletedEvent> context)
        {
            var msg = context.Message;

            await _repository.BeginTransactionAsync();
            // Same steps as before T23, moved to PurchaseOutcomes so the purchase recovery job reuses them.
            await PurchaseOutcomes.RecordCompletedAsync(_repository, msg.TransactionId, msg.ComicId);
            await _repository.CommitTransactionAsync();
        }
    }
}
