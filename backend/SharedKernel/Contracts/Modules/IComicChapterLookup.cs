namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction ComicAPI to ChapterAPI. Replaces the gRPC calls <c>ChapterGrpc.GetChapterCount</c> and
/// <c>ChapterGrpc.GetPurchasedComicIds</c> (T15, T16). Declaration only: nothing implements or uses it before T17.
/// </summary>
public interface IComicChapterLookup
{
    /// <summary>
    /// Number of chapters of a comic (0 when the comic has none or does not exist). Current gRPC server rule:
    /// a comic id that is not a valid Guid is rejected with <c>InvalidArgument</c>; a typed <see cref="Guid"/>
    /// cannot be invalid, and <see cref="Guid.Empty"/> is accepted today. Callers today treat any failure as a
    /// count of 0.
    /// </summary>
    Task<int> GetChapterCountAsync(Guid comicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of the comics in which the user bought at least one chapter. Current gRPC server rule: a user id
    /// of 0 or less is rejected with <c>InvalidArgument</c>. Callers today treat any failure as an error
    /// (<c>/api/comics/purchased</c> answers 503).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPurchasedComicIdsAsync(int userId, CancellationToken cancellationToken = default);
}
