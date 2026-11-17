using MediatR;
using SharedKernel.Contracts.Modules;
using SocialAPI.Application.Features.SocialActivities.Queries;
using SocialAPI.DTOs;

namespace SocialAPI.API.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC call <c>SocialActivity.GetUserActivities</c> (same query, same validation and
/// same time conversion as <c>SocialActivityGrpcService</c>). Any failure other than cancellation is wrapped in
/// <see cref="ModuleCallException"/>.
/// </summary>
public sealed class MissionSocialActivity : IMissionSocialActivity
{
    private readonly IMediator _mediator;

    public MissionSocialActivity(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task<UserActivities> GetUserActivitiesAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ModuleCallException("UserId is invalid.");
        }

        try
        {
            var activities = await _mediator.Send(new GetUserActivitiesQuery { NumericUserId = userId }, cancellationToken);
            return new UserActivities(ToSnapshots(activities.ReadChapters), ToSnapshots(activities.Comments));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("SocialAPI could not return the user activities.", ex);
        }
    }

    private static List<ActivitySnapshot> ToSnapshots(IEnumerable<ActivityItemDto> items) =>
        items
            .Select(item => new ActivitySnapshot(
                item.ActivityId,
                new DateTimeOffset(item.OccurredAt.ToUniversalTime()).ToUnixTimeSeconds()))
            .ToList();
}
