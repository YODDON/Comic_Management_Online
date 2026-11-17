using ChapterAPI.Application.Features.Chapters.Queries;
using MediatR;
using SharedKernel.Contracts.Modules;

namespace ChapterAPI.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC call <c>ChapterGrpc.GetUserPurchaseActivities</c> (same query, same validation
/// and same time conversion as <c>ChapterGrpcService</c>). Any failure other than cancellation is wrapped in
/// <see cref="ModuleCallException"/>.
/// </summary>
public sealed class MissionChapterActivity : IMissionChapterActivity
{
    private readonly IMediator _mediator;

    public MissionChapterActivity(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task<IReadOnlyList<PurchaseActivity>> GetUserPurchaseActivitiesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ModuleCallException("UserId is invalid.");
        }

        try
        {
            var purchases = await _mediator.Send(new GetUserPurchaseActivitiesQuery { UserId = userId }, cancellationToken);
            return purchases
                .Select(purchase => new PurchaseActivity(
                    purchase.ChapterId,
                    new DateTimeOffset(purchase.PurchasedAt.ToUniversalTime()).ToUnixTimeSeconds()))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("ChapterAPI could not return the purchase activities.", ex);
        }
    }
}
