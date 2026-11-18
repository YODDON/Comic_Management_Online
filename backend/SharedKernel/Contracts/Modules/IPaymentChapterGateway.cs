namespace SharedKernel.Contracts.Modules;

/// <summary>
/// Call direction PaymentAPI to ChapterAPI. Replaces the gRPC calls <c>ChapterGrpc.GetChapterInfo</c> and
/// <c>ChapterGrpc.UnlockChapter</c> (T15, T16). Declaration only: nothing implements or uses it before T17.
/// Sensitive area (chapter purchase): T17 needs an approved plan and the T37 tests.
/// </summary>
public interface IPaymentChapterGateway
{
    /// <summary>
    /// Price and display data of a chapter. A chapter that does not exist is a normal answer
    /// (<see cref="ChapterInfo.Exists"/> false, the other fields empty), not an error. Callers today turn a
    /// failed call into "503, try again later" (purchase) or skip the enrichment (deposit, webhook, history).
    /// </summary>
    Task<ChapterInfo> GetChapterInfoAsync(Guid chapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlocks a chapter for a user (the repair path for a purchase Payment already recorded; the saga unlocks
    /// through <c>UnlockChapterCommand</c>, not through this call). Not an error when already unlocked:
    /// <see cref="UnlockChapterResult.AlreadyPurchased"/> is set. Current gRPC server rule: ids that cannot be
    /// parsed give <c>Success = false</c>; typed ids cannot be unparseable, so that case disappears.
    /// </summary>
    Task<UnlockChapterResult> UnlockChapterAsync(
        int userId,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user has the chapter's unlock row (ChapterDB <c>UserPurchases</c>). Read only. Used by the
    /// purchase recovery job (T23). Any failure is a <see cref="ModuleCallException"/>.
    /// </summary>
    Task<bool> IsChapterUnlockedAsync(int userId, Guid chapterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fields of the gRPC <c>GetChapterInfoResponse</c>. When <see cref="Exists"/> is false, use
/// <see cref="NotFound"/>. <see cref="Status"/> is the chapter status name (Payment compares it with
/// "Published", ignoring case).
/// </summary>
public sealed record ChapterInfo(
    bool Exists,
    Guid ComicId,
    decimal UnitPrice,
    string Title,
    int ChapterNumber,
    string Slug,
    string Status)
{
    public static ChapterInfo NotFound { get; } =
        new(false, Guid.Empty, 0m, string.Empty, 0, string.Empty, string.Empty);
}

/// <summary>Fields of the gRPC <c>UnlockChapterResponse</c>.</summary>
public sealed record UnlockChapterResult(bool Success, bool AlreadyPurchased);
