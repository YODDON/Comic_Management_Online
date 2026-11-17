using ChapterAPI.Application.Features.Chapters.Queries;
using ChapterAPI.ModuleServices;
using ComicAPI.Application.DTOs;
using ComicAPI.Application.Features.Comics.Helpers;
using ComicAPI.Application.Features.Comics.Queries;
using ComicAPI.Domain.Entities;
using MediatR;
using SharedKernel.Contracts.Modules;
using UserAPI.API.ModuleServices;
using UserAPI.Application.Features.UserQueries.Queries;
using UserAPI.Domain.Entities;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// T17 group 1: ComicAPI to UserAPI and ComicAPI to ChapterAPI, now direct in-process calls. The callers' fallback
/// decisions (author "Unknown", chapter count 0, purchased comics 503) must behave as they did with gRPC.
/// </summary>
public class ComicModuleCallTests
{
    // ---- UserAPI side: ComicUserLookup ----

    [Fact]
    public async Task User_lookup_maps_every_field_and_sends_the_ids_to_the_query()
    {
        var mediator = new FakeMediator(request =>
        {
            Assert.Equal(new[] { 7, 9 }, ((GetUsersByIdsQuery)request).Ids);
            return new List<User>
            {
                new()
                {
                    Id = 7, Username = "alice", Email = "a@x.test", AvatarUrl = "http://img/a.png", IsActive = true,
                    UserRoles = new List<UserRole>
                    {
                        new() { Role = new Role { Name = "Admin" } },
                        new() { Role = new Role { Name = "Reader" } },
                    },
                },
            };
        });

        var result = await new ComicUserLookup(mediator).GetUsersByIdsAsync(new[] { 7, 9 });

        var user = Assert.Single(result);
        Assert.Equal(7, user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal("a@x.test", user.Email);
        Assert.Equal("http://img/a.png", user.AvatarUrl);
        Assert.True(user.IsActive);
        Assert.Equal(new[] { "Admin", "Reader" }, user.Roles);
    }

    [Fact]
    public async Task User_lookup_leaves_out_unknown_ids_and_an_empty_avatar_is_empty_not_null()
    {
        var mediator = new FakeMediator(_ => new List<User>
        {
            new() { Id = 1, Username = "bob", AvatarUrl = null! },
        });

        var result = await new ComicUserLookup(mediator).GetUsersByIdsAsync(new[] { 1, 2, 3 });

        var user = Assert.Single(result);
        Assert.Equal(string.Empty, user.AvatarUrl);
        Assert.Empty(user.Roles);
    }

    [Fact]
    public async Task User_lookup_wraps_a_failure_in_ModuleCallException_and_keeps_the_cause()
    {
        var cause = new InvalidOperationException("db down");
        var lookup = new ComicUserLookup(new FakeMediator(_ => throw cause));

        var ex = await Assert.ThrowsAsync<ModuleCallException>(() => lookup.GetUsersByIdsAsync(new[] { 1 }));

        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public async Task User_lookup_does_not_wrap_cancellation()
    {
        var lookup = new ComicUserLookup(new FakeMediator(_ => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup.GetUsersByIdsAsync(new[] { 1 }));
    }

    // ---- ChapterAPI side: ComicChapterLookup ----

    [Fact]
    public async Task Chapter_lookup_returns_the_count_for_the_comic()
    {
        var comicId = Guid.NewGuid();
        var mediator = new FakeMediator(request =>
        {
            Assert.Equal(comicId, ((GetChapterCountQuery)request).ComicId);
            return 12;
        });

        Assert.Equal(12, await new ComicChapterLookup(mediator).GetChapterCountAsync(comicId));
    }

    [Fact]
    public async Task Chapter_lookup_accepts_an_empty_comic_id_like_the_grpc_server_did()
    {
        var mediator = new FakeMediator(_ => 0);

        Assert.Equal(0, await new ComicChapterLookup(mediator).GetChapterCountAsync(Guid.Empty));
    }

    [Fact]
    public async Task Chapter_lookup_wraps_a_count_failure()
    {
        var lookup = new ComicChapterLookup(new FakeMediator(_ => throw new InvalidOperationException("db down")));

        await Assert.ThrowsAsync<ModuleCallException>(() => lookup.GetChapterCountAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Chapter_lookup_returns_the_purchased_comic_ids()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var mediator = new FakeMediator(request =>
        {
            Assert.Equal(5, ((GetPurchasedComicIdsQuery)request).UserId);
            return ids;
        });

        Assert.Equal(ids, await new ComicChapterLookup(mediator).GetPurchasedComicIdsAsync(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Chapter_lookup_rejects_a_user_id_of_zero_or_less_without_calling_the_query(int userId)
    {
        var queryRan = false;
        var mediator = new FakeMediator(_ =>
        {
            queryRan = true;
            return new List<Guid>();
        });

        var ex = await Assert.ThrowsAsync<ModuleCallException>(() => new ComicChapterLookup(mediator).GetPurchasedComicIdsAsync(userId));

        Assert.False(queryRan);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Chapter_lookup_wraps_a_purchased_ids_failure()
    {
        var lookup = new ComicChapterLookup(new FakeMediator(_ => throw new InvalidOperationException("db down")));

        await Assert.ThrowsAsync<ModuleCallException>(() => lookup.GetPurchasedComicIdsAsync(5));
    }

    // ---- ComicAPI side: the callers' existing decisions ----

    [Fact]
    public async Task Author_name_comes_from_the_comic_when_set_and_the_user_lookup_is_not_called()
    {
        var comic = NewComic(ownerId: 3, author: "  Oda  ");
        var dto = new ComicSummaryDto { Id = comic.Id };

        await ComicHelper.EnrichComicsWithAuthorNamesAsync(new ThrowingUserLookup(), new List<ComicSummaryDto> { dto }, new List<Comic> { comic });

        Assert.Equal("Oda", dto.AuthorName);
    }

    [Fact]
    public async Task Author_name_comes_from_the_owner_when_the_comic_has_none_and_Unknown_when_the_owner_is_missing()
    {
        var known = NewComic(ownerId: 3, author: "");
        var missing = NewComic(ownerId: 4, author: " ");
        var dtos = new List<ComicSummaryDto> { new() { Id = known.Id }, new() { Id = missing.Id } };
        var lookup = new FixedUserLookup(new UserSummary(3, "carol", "c@x.test", "", true, Array.Empty<string>()));

        await ComicHelper.EnrichComicsWithAuthorNamesAsync(lookup, dtos, new List<Comic> { known, missing });

        Assert.Equal("carol", dtos[0].AuthorName);
        Assert.Equal("Unknown", dtos[1].AuthorName);
        Assert.Equal(new[] { 3, 4 }, lookup.RequestedIds);
    }

    [Fact]
    public async Task Author_name_is_Unknown_for_every_comic_without_author_when_the_user_lookup_fails()
    {
        var withAuthor = NewComic(ownerId: 3, author: "Oda");
        var without = NewComic(ownerId: 4, author: "");
        var dtos = new List<ComicSummaryDto> { new() { Id = withAuthor.Id }, new() { Id = without.Id } };

        await ComicHelper.EnrichComicsWithAuthorNamesAsync(new ThrowingUserLookup(), dtos, new List<Comic> { withAuthor, without });

        Assert.Equal("Oda", dtos[0].AuthorName);
        Assert.Equal("Unknown", dtos[1].AuthorName);
    }

    [Fact]
    public async Task Purchased_comics_answers_503_when_the_chapter_lookup_fails()
    {
        // The repository and mapper are not reached on this path.
        var handler = new GetPurchasedComicsQueryHandler(null!, null!, new ThrowingUserLookup(), new FailingChapterLookup());

        var response = await handler.Handle(new GetPurchasedComicsQuery { UserId = 5, PageNumber = 1, PageSize = 10 }, default);

        Assert.False(response.Success);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("Could not retrieve purchased comics from ChapterAPI.", response.Message);
    }

    [Fact]
    public async Task Purchased_comics_is_an_empty_page_when_only_empty_guids_come_back()
    {
        var handler = new GetPurchasedComicsQueryHandler(null!, null!, new ThrowingUserLookup(), new FailingChapterLookup(Guid.Empty, Guid.Empty));

        var response = await handler.Handle(new GetPurchasedComicsQuery { UserId = 5, PageNumber = 0, PageSize = 500 }, default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Purchased comics retrieved.", response.Message);
        Assert.Empty(response.Data!.Items);
        Assert.Equal(100, response.Data.PageSize);
    }

    // ---- helpers ----

    private static Comic NewComic(int ownerId, string author) =>
        new() { Id = Guid.NewGuid(), OwnerId = ownerId, Author = author };

    private sealed class ThrowingUserLookup : IComicUserLookup
    {
        public Task<IReadOnlyList<UserSummary>> GetUsersByIdsAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default) =>
            throw new ModuleCallException("UserAPI down");
    }

    private sealed class FixedUserLookup : IComicUserLookup
    {
        private readonly UserSummary[] _users;
        public IReadOnlyCollection<int> RequestedIds { get; private set; } = Array.Empty<int>();

        public FixedUserLookup(params UserSummary[] users) => _users = users;

        public Task<IReadOnlyList<UserSummary>> GetUsersByIdsAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
        {
            RequestedIds = userIds;
            return Task.FromResult<IReadOnlyList<UserSummary>>(_users.Where(u => userIds.Contains(u.Id)).ToList());
        }
    }

    /// <summary>Throws when no ids are given, otherwise returns the given ids.</summary>
    private sealed class FailingChapterLookup : IComicChapterLookup
    {
        private readonly Guid[] _ids;
        public FailingChapterLookup(params Guid[] ids) => _ids = ids;

        public Task<int> GetChapterCountAsync(Guid comicId, CancellationToken cancellationToken = default) =>
            throw new ModuleCallException("ChapterAPI down");

        public Task<IReadOnlyList<Guid>> GetPurchasedComicIdsAsync(int userId, CancellationToken cancellationToken = default) =>
            _ids.Length == 0
                ? throw new ModuleCallException("ChapterAPI down")
                : Task.FromResult<IReadOnlyList<Guid>>(_ids);
    }

    /// <summary>Answers every request with the result of the function (which may throw).</summary>
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
