namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction MissionAPI to ChapterAPI. Replaces the gRPC call <c>ChapterGrpc.GetUserPurchaseActivities</c>
/// (T15, T16). Declaration only: nothing implements or uses it before T17.
/// </summary>
public interface IMissionChapterActivity
{
    /// <summary>
    /// Chapters the user has bought, with the purchase time. Current gRPC server rule: a user id of 0 or less is
    /// rejected with <c>InvalidArgument</c>. <c>SyncCommand</c> logs a warning and carries on when the call fails.
    /// </summary>
    Task<IReadOnlyList<PurchaseActivity>> GetUserPurchaseActivitiesAsync(
        int userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fields of the gRPC <c>PurchaseActivity</c>. The time stays in Unix seconds as on the wire;
/// <c>SyncCommand</c> uses "now" when the value is 0 or less.
/// </summary>
public sealed record PurchaseActivity(Guid ChapterId, long PurchasedAtUnixSeconds);
