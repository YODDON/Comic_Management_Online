using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Features.Payments.Queries;
using PaymentAPI.Interfaces;
using SharedKernel.Enums;

namespace Payment.Characterization.Tests;

/// <summary>
/// T17 group 3: the history queries ask ChapterAPI for the chapter of every purchase transaction, several calls at
/// the same time (<c>Task.WhenAll</c>). These tests pin that all of them are answered with the right chapter, and
/// that a ChapterAPI failure leaves the history intact (the catch around the call). They run the REAL Chapter
/// gateway against the Chapter database; before T17 nothing covered this path.
/// </summary>
[Collection(T37Collection.Name)]
public class HistoryEnrichmentTests
{
    private const int PurchaseCount = 8;
    private const string GetChapterInfo = "/chapter.ChapterGrpc/GetChapterInfo";

    private readonly T37Fixture _fixture;
    public HistoryEnrichmentTests(T37Fixture fixture) => _fixture = fixture;

    private static async Task<(Guid ChapterId, Guid ComicId, string Title, int Number, string Slug)> SeedChapterAsync(
        TestWorld world, int number)
    {
        using var scope = world.ChapterScope();
        var db = scope.ServiceProvider.GetRequiredService<ChapterAPI.Data.ChapterDbContext>();
        var chapter = new ChapterAPI.Entities.Chapter
        {
            ComicId = Guid.NewGuid(),
            Title = $"History chapter {number}",
            Slug = $"history-{Guid.NewGuid():N}",
            ChapterNumber = number,
            UnitPrice = 50m,
            Status = "Published",
        };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();
        return (chapter.Id, chapter.ComicId, chapter.Title, chapter.ChapterNumber, chapter.Slug);
    }

    private static async Task SeedPurchaseTransactionAsync(TestWorld world, int user, Guid chapterId)
    {
        using var scope = world.PaymentScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        await repository.BeginTransactionAsync();
        await repository.AddTransactionAsync(new PaymentAPI.Entities.Transaction
        {
            UserId = user,
            ChapterId = chapterId,
            Type = TransactionType.Purchase,
            Amount = 50m,
            CurrencyType = "Dâu",
            Status = TransactionStatus.Completed,
        });
        await repository.CommitTransactionAsync();
    }

    private static async Task<List<PaymentAPI.DTOs.TransactionDto>> HistoryAsync(TestWorld world, int user)
    {
        using var scope = world.PaymentScope();
        var response = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new GetTransactionsQuery { UserId = user, PageNumber = 1, PageSize = 50 });
        Assert.True(response.Success);
        return response.Data!.Items.ToList();
    }

    [Fact]
    public async Task History_of_many_purchases_gets_every_chapters_own_title_number_slug_and_comic()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapters = new List<(Guid ChapterId, Guid ComicId, string Title, int Number, string Slug)>();
        for (var i = 1; i <= PurchaseCount; i++)
        {
            var chapter = await SeedChapterAsync(world, i);
            chapters.Add(chapter);
            await SeedPurchaseTransactionAsync(world, user, chapter.ChapterId);
        }

        var callsBefore = world.Grpc.Calls.Count(c => c == GetChapterInfo);
        var history = await HistoryAsync(world, user);

        Assert.Equal(PurchaseCount, history.Count);
        foreach (var chapter in chapters)
        {
            var item = Assert.Single(history, t => t.ChapterId == chapter.ChapterId);
            Assert.Equal(chapter.Title, item.ChapterTitle);
            Assert.Equal(chapter.Number, item.ChapterNumber);
            Assert.Equal(chapter.Slug, item.ChapterSlug);
            Assert.Equal(chapter.ComicId, item.ComicId);
        }
        Assert.Equal(PurchaseCount, world.Grpc.Calls.Count(c => c == GetChapterInfo) - callsBefore);
    }

    [Fact]
    public async Task History_of_a_missing_chapter_stays_without_chapter_details()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var missing = Guid.NewGuid();
        await SeedPurchaseTransactionAsync(world, user, missing);

        var item = Assert.Single(await HistoryAsync(world, user));

        Assert.Equal(missing, item.ChapterId);
        Assert.Null(item.ChapterTitle);
        Assert.Null(item.ChapterSlug);
        Assert.Null(item.ComicId);
    }

    [Fact]
    public async Task History_is_still_returned_when_ChapterAPI_is_unavailable()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await SeedChapterAsync(world, 1);
        await SeedPurchaseTransactionAsync(world, user, chapter.ChapterId);
        world.Grpc.Unavailable.Add(GetChapterInfo);

        var item = Assert.Single(await HistoryAsync(world, user));

        Assert.Equal(chapter.ChapterId, item.ChapterId);
        Assert.Null(item.ChapterTitle);
        Assert.Null(item.ComicId);
    }
}
