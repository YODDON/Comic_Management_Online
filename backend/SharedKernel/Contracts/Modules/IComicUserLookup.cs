namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction ComicAPI to UserAPI. Replaces the gRPC call <c>UserService.GetUsersByIds</c> (T15, T16).
/// Declaration only: nothing implements or uses it before T17.
/// </summary>
public interface IComicUserLookup
{
    /// <summary>
    /// Returns the users that exist for the given ids. Ids with no user are left out of the result (no error).
    /// Current gRPC server (<c>UserGrpcService.GetUsersByIds</c>): ids that are not numbers are skipped.
    /// Callers today treat any failure as "author unknown".
    /// </summary>
    Task<IReadOnlyList<UserSummary>> GetUsersByIdsAsync(
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Fields of the gRPC <c>UserResponse</c>. <see cref="AvatarUrl"/> is empty (not null) when the user has none.</summary>
public sealed record UserSummary(
    int Id,
    string Username,
    string Email,
    string AvatarUrl,
    bool IsActive,
    IReadOnlyList<string> Roles);
