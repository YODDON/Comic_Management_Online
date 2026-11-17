using ChapterAPI.Application.Features.Chapters.Queries;
using ChapterAPI.Entities;
using ChapterAPI.ModuleServices;
using MediatR;
using Microsoft.Extensions.Logging;
using MissionAPI.Application.Features.Missions.Commands;
using MissionAPI.Entities;
using MissionAPI.Interfaces;
using SharedKernel.Contracts.Modules;
using SharedKernel.Enums;
using SocialAPI.API.ModuleServices;
using SocialAPI.Application.Features.SocialActivities.Queries;
using SocialAPI.DTOs;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// T17 group 2: MissionAPI to ChapterAPI and MissionAPI to SocialAPI, now direct in-process calls. SyncCommand must
/// keep logging a warning and carrying on when one side fails, as it did when it caught RpcException.
/// </summary>
public class MissionModuleCallTests
{
    private static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly long MomentUnix = new DateTimeOffset(Moment).ToUnixTimeSeconds();

    // ---- ChapterAPI side: MissionChapterActivity ----

    [Fact]
    public async Task Chapter_activity_maps_chapter_id_and_unix_seconds_and_sends_the_user_id()
    {
        var chapterId = Guid.NewGuid();
        var mediator = new FakeMediator(request =>
        {
            Assert.Equal(5, ((GetUserPurchaseActivitiesQuery)request).UserId);
            return new List<UserPurchase> { new() { ChapterId = chapterId, PurchasedAt = Moment } };
        });

        var result = await new MissionChapterActivity(mediator).GetUserPurchaseActivitiesAsync(5);

        var activity = Assert.Single(result);
        Assert.Equal(chapterId, activity.ChapterId);
        Assert.Equal(MomentUnix, activity.PurchasedAtUnixSeconds);
    }

    [Fact]
    public async Task Chapter_activity_converts_a_local_time_to_utc_like_the_grpc_server_did()
    {
        var local = Moment.ToLocalTime();
        var mediator = new FakeMediator(_ => new List<UserPurchase> { new() { ChapterId = Guid.NewGuid(), PurchasedAt = local } });

        var result = await new MissionChapterActivity(mediator).GetUserPurchaseActivitiesAsync(5);

        Assert.Equal(MomentUnix, Assert.Single(result).PurchasedAtUnixSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Chapter_activity_rejects_a_user_id_of_zero_or_less_without_calling_the_query(int userId)
    {
        var queryRan = false;
        var activity = new MissionChapterActivity(new FakeMediator(_ =>
        {
            queryRan = true;
            return new List<UserPurchase>();
        }));

        await Assert.ThrowsAsync<ModuleCallException>(() => activity.GetUserPurchaseActivitiesAsync(userId));
        Assert.False(queryRan);
    }

    [Fact]
    public async Task Chapter_activity_wraps_an_arbitrary_failure_and_keeps_the_cause()
    {
        var cause = new InvalidOperationException("db down");
        var activity = new MissionChapterActivity(new FakeMediator(_ => throw cause));

        var ex = await Assert.ThrowsAsync<ModuleCallException>(() => activity.GetUserPurchaseActivitiesAsync(5));
        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public async Task Chapter_activity_does_not_wrap_cancellation()
    {
        var activity = new MissionChapterActivity(new FakeMediator(_ => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => activity.GetUserPurchaseActivitiesAsync(5));
    }

    // ---- SocialAPI side: MissionSocialActivity ----

    [Fact]
    public async Task Social_activity_maps_both_lists_and_sends_the_user_id()
    {
        var read = Guid.NewGuid();
        var comment = Guid.NewGuid();
        var mediator = new FakeMediator(request =>
        {
            Assert.Equal(8, ((GetUserActivitiesQuery)request).NumericUserId);
            return new SocialActivitySnapshotDto
            {
                ReadChapters = [new ActivityItemDto(read, Moment)],
                Comments = [new ActivityItemDto(comment, Moment.AddSeconds(10))],
            };
        });

        var result = await new MissionSocialActivity(mediator).GetUserActivitiesAsync(8);

        var readItem = Assert.Single(result.ReadChapters);
        Assert.Equal(read, readItem.ActivityId);
        Assert.Equal(MomentUnix, readItem.OccurredAtUnixSeconds);
        var commentItem = Assert.Single(result.Comments);
        Assert.Equal(comment, commentItem.ActivityId);
        Assert.Equal(MomentUnix + 10, commentItem.OccurredAtUnixSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Social_activity_rejects_a_user_id_of_zero_or_less_without_calling_the_query(int userId)
    {
        var queryRan = false;
        var activity = new MissionSocialActivity(new FakeMediator(_ =>
        {
            queryRan = true;
            return new SocialActivitySnapshotDto();
        }));

        await Assert.ThrowsAsync<ModuleCallException>(() => activity.GetUserActivitiesAsync(userId));
        Assert.False(queryRan);
    }

    [Fact]
    public async Task Social_activity_wraps_an_arbitrary_failure_and_keeps_the_cause()
    {
        var cause = new NullReferenceException();
        var activity = new MissionSocialActivity(new FakeMediator(_ => throw cause));

        var ex = await Assert.ThrowsAsync<ModuleCallException>(() => activity.GetUserActivitiesAsync(8));
        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public async Task Social_activity_wraps_a_null_answer()
    {
        var activity = new MissionSocialActivity(new FakeMediator(_ => null!));

        await Assert.ThrowsAsync<ModuleCallException>(() => activity.GetUserActivitiesAsync(8));
    }

    [Fact]
    public async Task Social_activity_does_not_wrap_cancellation()
    {
        var activity = new MissionSocialActivity(new FakeMediator(_ => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => activity.GetUserActivitiesAsync(8));
    }

    // ---- MissionAPI side: SyncCommandHandler ----

    [Fact]
    public async Task Sync_records_purchases_reads_and_comments_with_their_times()
    {
        var purchase = Guid.NewGuid();
        var read = Guid.NewGuid();
        var comment = Guid.NewGuid();
        var repository = new RecordingRepository();
        var handler = new SyncCommandHandler(
            repository,
            new FakeChapterActivity(() => new[] { new PurchaseActivity(purchase, MomentUnix) }),
            new FakeSocialActivity(() => new UserActivities(
                new[] { new ActivitySnapshot(read, MomentUnix + 1) },
                new[] { new ActivitySnapshot(comment, 0) })),
            new CapturingLogger());

        var before = DateTime.UtcNow;
        await handler.Handle(new SyncCommand { UserId = 5 }, CancellationToken.None);

        Assert.Equal(3, repository.Recorded.Count);
        Assert.Contains(repository.Recorded, r => r.Type == MissionType.PurchaseChapter && r.ActivityId == purchase && r.At == Moment);
        Assert.Contains(repository.Recorded, r => r.Type == MissionType.ReadChapter && r.ActivityId == read && r.At == Moment.AddSeconds(1));
        // time 0 means "now", as before
        Assert.Contains(repository.Recorded, r => r.Type == MissionType.LeaveComment && r.ActivityId == comment && r.At >= before);
        Assert.All(repository.Recorded, r => Assert.Equal(5, r.UserId));
    }

    [Fact]
    public async Task Sync_logs_a_warning_and_still_syncs_social_when_the_chapter_side_throws_an_arbitrary_exception()
    {
        var read = Guid.NewGuid();
        var repository = new RecordingRepository();
        var logger = new CapturingLogger();
        // The real implementation wraps the arbitrary exception; mirror that through the real class.
        var chapterSide = new MissionChapterActivity(new FakeMediator(_ => throw new InvalidOperationException("boom")));
        var handler = new SyncCommandHandler(
            repository,
            chapterSide,
            new FakeSocialActivity(() => new UserActivities(new[] { new ActivitySnapshot(read, MomentUnix) }, Array.Empty<ActivitySnapshot>())),
            logger);

        await handler.Handle(new SyncCommand { UserId = 5 }, CancellationToken.None);

        var recorded = Assert.Single(repository.Recorded);
        Assert.Equal(MissionType.ReadChapter, recorded.Type);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("chapter purchases", warning);
    }

    [Fact]
    public async Task Sync_logs_a_warning_and_still_syncs_purchases_when_the_social_side_throws_an_arbitrary_exception()
    {
        var purchase = Guid.NewGuid();
        var repository = new RecordingRepository();
        var logger = new CapturingLogger();
        var socialSide = new MissionSocialActivity(new FakeMediator(_ => throw new InvalidOperationException("boom")));
        var handler = new SyncCommandHandler(
            repository,
            new FakeChapterActivity(() => new[] { new PurchaseActivity(purchase, MomentUnix) }),
            socialSide,
            logger);

        await handler.Handle(new SyncCommand { UserId = 5 }, CancellationToken.None);

        var recorded = Assert.Single(repository.Recorded);
        Assert.Equal(MissionType.PurchaseChapter, recorded.Type);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("social activities", warning);
    }

    [Fact]
    public async Task Sync_does_not_swallow_cancellation()
    {
        var handler = new SyncCommandHandler(
            new RecordingRepository(),
            new FakeChapterActivity(() => throw new OperationCanceledException()),
            new FakeSocialActivity(() => new UserActivities(Array.Empty<ActivitySnapshot>(), Array.Empty<ActivitySnapshot>())),
            new CapturingLogger());

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.Handle(new SyncCommand { UserId = 5 }, CancellationToken.None));
    }

    // ---- fakes ----

    private sealed class FakeChapterActivity : IMissionChapterActivity
    {
        private readonly Func<IReadOnlyList<PurchaseActivity>> _answer;
        public FakeChapterActivity(Func<IReadOnlyList<PurchaseActivity>> answer) => _answer = answer;

        public Task<IReadOnlyList<PurchaseActivity>> GetUserPurchaseActivitiesAsync(int userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answer());
    }

    private sealed class FakeSocialActivity : IMissionSocialActivity
    {
        private readonly Func<UserActivities> _answer;
        public FakeSocialActivity(Func<UserActivities> answer) => _answer = answer;

        public Task<UserActivities> GetUserActivitiesAsync(int userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answer());
    }

    private sealed class CapturingLogger : ILogger<SyncCommandHandler>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed record Recorded(int UserId, MissionType Type, Guid ActivityId, DateTime At);

    private sealed class RecordingRepository : IMissionRepository
    {
        public List<Recorded> Recorded { get; } = new();

        public Task<IReadOnlyList<UserMission>> RecordActivityAsync(int userId, MissionType type, Guid activityId, DateTime occurredAt)
        {
            Recorded.Add(new Recorded(userId, type, activityId, occurredAt));
            return Task.FromResult<IReadOnlyList<UserMission>>(Array.Empty<UserMission>());
        }

        public Task<IEnumerable<Mission>> GetAllMissionsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Mission>> GetAllMissionsForAdminAsync() => throw new NotSupportedException();
        public Task<Mission?> GetMissionByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<Mission> CreateMissionAsync(Mission mission) => throw new NotSupportedException();
        public Task UpdateMissionAsync(Mission mission) => throw new NotSupportedException();
        public Task<IEnumerable<UserMission>> GetUserMissionsAsync(int userId) => throw new NotSupportedException();
        public Task<UserMission?> GetUserMissionAsync(int userId, Guid missionId) => throw new NotSupportedException();
        public Task<UserMission> AddUserMissionAsync(UserMission userMission) => throw new NotSupportedException();
        public Task UpdateUserMissionAsync(UserMission userMission) => throw new NotSupportedException();
        public Task SaveChangesAsync() => throw new NotSupportedException();
    }

    private sealed class FakeMediator : IMediator
    {
        private readonly Func<object, object> _answer;
        public FakeMediator(Func<object, object> answer) => _answer = answer;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)_answer(request));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
