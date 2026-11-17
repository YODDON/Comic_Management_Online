namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction MissionAPI to SocialAPI. Replaces the gRPC call <c>SocialActivity.GetUserActivities</c>
/// (T15, T16). Declaration only: nothing implements or uses it before T17.
/// </summary>
public interface IMissionSocialActivity
{
    /// <summary>
    /// Chapters the user has read and comments the user has left. Current gRPC server rule: a user id of 0 or
    /// less is rejected with <c>InvalidArgument</c>. <c>SyncCommand</c> logs a warning and carries on when the
    /// call fails.
    /// </summary>
    Task<UserActivities> GetUserActivitiesAsync(int userId, CancellationToken cancellationToken = default);
}

/// <summary>Fields of the gRPC <c>GetUserActivitiesResponse</c>.</summary>
public sealed record UserActivities(
    IReadOnlyList<ActivitySnapshot> ReadChapters,
    IReadOnlyList<ActivitySnapshot> Comments);

/// <summary>
/// Fields of the gRPC <c>ActivitySnapshot</c>. The time stays in Unix seconds as on the wire;
/// <c>SyncCommand</c> uses "now" when the value is 0 or less.
/// </summary>
public sealed record ActivitySnapshot(Guid ActivityId, long OccurredAtUnixSeconds);
