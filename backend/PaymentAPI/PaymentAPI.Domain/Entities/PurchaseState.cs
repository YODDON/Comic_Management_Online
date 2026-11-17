using System;
using MassTransit;

namespace PaymentAPI.Domain.Entities
{
    public class PurchaseState : SagaStateMachineInstance
    {
        public Guid CorrelationId { get; set; }
        
        public string CurrentState { get; set; } = string.Empty;
        
        public int UserId { get; set; }
        
        public Guid ComicId { get; set; }

        public Guid ChapterId { get; set; }
        
        public decimal Price { get; set; }
        
        public Guid TransactionId { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        
        // Optional error reason tracking
        public string? ErrorReason { get; set; }

        // The original SubmitPurchaseCommand request (IRequestClient in PurchaseChapterCommandHandler), so the saga can
        // answer it from a later event. Null for sagas created before these columns existed, or started without a request.
        public Guid? RequestId { get; set; }
        public Uri? ResponseAddress { get; set; }
    }
}
