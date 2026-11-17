using System.Threading.Tasks;
using MassTransit;
using PaymentAPI.Application.Services;
using PaymentAPI.Interfaces;
using SharedKernel.Contracts.Purchase;

namespace PaymentAPI.Application.Consumers
{
    public class PurchaseFailedConsumer : IConsumer<PurchaseFailedEvent>
    {
        private readonly IPaymentRepository _repository;

        public PurchaseFailedConsumer(IPaymentRepository repository)
        {
            _repository = repository;
        }

        public async Task Consume(ConsumeContext<PurchaseFailedEvent> context)
        {
            var msg = context.Message;

            await _repository.BeginTransactionAsync();
            // Same steps as before T23, moved to PurchaseOutcomes so the purchase recovery job reuses them.
            await PurchaseOutcomes.RecordFailedAsync(_repository, msg.TransactionId, msg.Reason);
            await _repository.CommitTransactionAsync();
        }
    }
}
