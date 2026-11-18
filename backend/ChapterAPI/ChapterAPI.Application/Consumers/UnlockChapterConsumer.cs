using System;
using System.Threading.Tasks;
using MassTransit;
using SharedKernel.Contracts.Purchase;
using ChapterAPI.Interfaces;

namespace ChapterAPI.Application.Consumers
{
    public class UnlockChapterConsumer : IConsumer<UnlockChapterCommand>
    {
        private readonly IChapterRepository _repository;

        public UnlockChapterConsumer(IChapterRepository repository)
        {
            _repository = repository;
        }

        public async Task Consume(ConsumeContext<UnlockChapterCommand> context)
        {
            var msg = context.Message;

            // T43: exactly one of ChapterUnlockedEvent / ChapterUnlockFailedEvent is published per command, and
            // ChapterUnlockedEvent only after the unlock row is saved. Before T43 "unlocked" was published before the
            // save, so a failed save (or a missing chapter) finalized the saga as a success without an unlock.
            string? failure;
            try
            {
                // true: the unlock row was added, or it already existed. false: the chapter does not exist.
                var unlocked = await _repository.UnlockChapterAsync(msg.UserId, msg.ChapterId);
                if (unlocked)
                {
                    await _repository.SaveChangesAsync();
                    failure = null;
                }
                else
                {
                    failure = "Chapter không tồn tại.";
                }
            }
            catch (Exception ex)
            {
                // Also a unique-index clash on (UserId, ChapterId) with an unlock row saved at the same time by another
                // purchase: this purchase is then refunded by the saga.
                failure = $"Lỗi hệ thống khi mở khóa chapter: {ex.Message}";
            }

            if (failure == null)
            {
                await context.Publish(new ChapterUnlockedEvent(msg.CorrelationId));
            }
            else
            {
                await context.Publish(new ChapterUnlockFailedEvent(msg.CorrelationId, failure));
            }
        }
    }
}
