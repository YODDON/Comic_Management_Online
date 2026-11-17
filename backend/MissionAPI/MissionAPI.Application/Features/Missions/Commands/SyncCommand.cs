using System;
using MassTransit;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using MissionAPI.Interfaces;
using SharedKernel.Contracts.Modules;
using SharedKernel.Enums;

namespace MissionAPI.Application.Features.Missions.Commands
{
    public class SyncCommand : IRequest<Unit>
    {
        public int UserId { get; set; }
    }

    public class SyncCommandHandler : IRequestHandler<SyncCommand, Unit>
    {
        private readonly IMissionRepository _repository;
        private readonly IMissionChapterActivity _chapterActivity;
        private readonly IMissionSocialActivity _socialActivity;
        private readonly ILogger<SyncCommandHandler> _logger;

        public SyncCommandHandler(
            IMissionRepository repository,
            IMissionChapterActivity chapterActivity,
            IMissionSocialActivity socialActivity,
            ILogger<SyncCommandHandler> logger)
        {
            _repository = repository;
            _chapterActivity = chapterActivity;
            _socialActivity = socialActivity;
            _logger = logger;
        }

        public async Task<Unit> Handle(SyncCommand request, CancellationToken cancellationToken)
        {
            await SyncPurchasesAsync(request.UserId, cancellationToken);
            await SyncSocialActivitiesAsync(request.UserId, cancellationToken);
            return Unit.Value;
        }

        private async Task SyncPurchasesAsync(int userId, CancellationToken cancellationToken)
        {
            try
            {
                var activities = await _chapterActivity.GetUserPurchaseActivitiesAsync(userId, cancellationToken);
                foreach (var activity in activities)
                {
                    await _repository.RecordActivityAsync(
                        userId,
                        MissionType.PurchaseChapter,
                        activity.ChapterId,
                        FromUnixTime(activity.PurchasedAtUnixSeconds));
                }
            }
            catch (ModuleCallException exception)
            {
                _logger.LogWarning(exception, "Could not synchronize chapter purchases for user {UserId}.", userId);
            }
        }

        private async Task SyncSocialActivitiesAsync(int userId, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _socialActivity.GetUserActivitiesAsync(userId, cancellationToken);
                foreach (var activity in response.ReadChapters)
                {
                    await _repository.RecordActivityAsync(
                        userId,
                        MissionType.ReadChapter,
                        activity.ActivityId,
                        FromUnixTime(activity.OccurredAtUnixSeconds));
                }

                foreach (var activity in response.Comments)
                {
                    await _repository.RecordActivityAsync(
                        userId,
                        MissionType.LeaveComment,
                        activity.ActivityId,
                        FromUnixTime(activity.OccurredAtUnixSeconds));
                }
            }
            catch (ModuleCallException exception)
            {
                _logger.LogWarning(exception, "Could not synchronize social activities for user {UserId}.", userId);
            }
        }

        private static DateTime FromUnixTime(long value) => value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime
            : DateTime.UtcNow;
    }
}
