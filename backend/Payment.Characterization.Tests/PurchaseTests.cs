using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Payment.Characterization.Tests.Infrastructure;
using PaymentAPI.Application.Features.Payments.Commands;
using PaymentAPI.Data;
using PaymentAPI.DTOs;
using SharedKernel.Responses;

namespace Payment.Characterization.Tests;

/// <summary>
/// T37 characterization: POST /payments/purchased-chapter paths that end BEFORE the saga is started
/// (already owned, chapter missing / unpublished / free, ChapterAPI unreachable). Expected values were observed by
/// running the current code; the saga path itself is pinned in PurchaseKnownBugTests.
/// </summary>
[Collection(T37Collection.Name)]
public class PurchaseTests
{
    private const string GetChapterInfo = "/chapter.ChapterGrpc/GetChapterInfo";
    private const string UnlockChapter = "/chapter.ChapterGrpc/UnlockChapter";

    private readonly T37Fixture _fixture;
    public PurchaseTests(T37Fixture fixture) => _fixture = fixture;

    private static async Task<ApiResponse<ChapterPurchaseResultDto>> PurchaseAsync(TestWorld world, int user, Guid chapter)
    {
        using var scope = world.PaymentScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new PurchaseChapterCommand { UserId = user, ChapterId = chapter });
    }

    private static async Task SeedPaymentPurchaseAsync(TestWorld world, int user, Guid chapter, decimal price)
    {
        using var scope = world.PaymentScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        db.UserPurchases.Add(new PaymentAPI.Entities.UserPurchase { UserId = user, ComicId = Guid.NewGuid(), ChapterId = chapter, Price = price });
        await db.SaveChangesAsync();
    }

    private static async Task AssertNothingChargedAsync(TestWorld world, int user, Guid chapter, decimal? balance)
    {
        Assert.Empty(await world.TransactionsAsync(user));
        Assert.Empty(await world.SagasForAsync(user));
        Assert.Equal(balance, await world.BalanceAsync(user));
        Assert.Equal(balance is null ? 0 : 1, (await world.LedgerAsync(user)).Count);
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
    }

    [Fact]
    public async Task Already_owned_chapter_is_re_unlocked_in_ChapterAPI_and_not_charged_again()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);
        await SeedPaymentPurchaseAsync(world, user, chapter, 100m);

        using var scope = world.PaymentScope();
        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), user.ToString())
            .PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = chapter });

        Assert.Equal(200, Controllers.StatusOf(result));
        var body = Assert.IsType<ApiResponse<ChapterPurchaseResultDto>>(((ObjectResult)result).Value);
        Assert.True(body.Success);
        Assert.Equal("Chapter này đã được mở khóa trước đó.", body.Message);
        Assert.Equal(chapter, body.Data!.ChapterId);
        Assert.Equal(100m, body.Data.Price);              // price stored on the old purchase, not today's price
        Assert.Null(body.Data.RemainingBalance);
        Assert.True(body.Data.AlreadyPurchased);

        Assert.True(await world.ChapterUnlockedAsync(user, chapter)); // the "repair" unlock ran
        Assert.Empty(await world.TransactionsAsync(user));
        Assert.Equal(500m, await world.BalanceAsync(user));
        Assert.Equal(new[] { UnlockChapter }, world.Grpc.Calls.Where(c => c.Contains("ChapterGrpc")).TakeLast(1));
    }

    [Fact]
    public async Task Already_owned_but_chapter_missing_in_ChapterAPI_returns_502()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = Guid.NewGuid(); // owned in Payment, unknown to Chapter
        await SeedPaymentPurchaseAsync(world, user, chapter, 100m);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.False(response.Success);
        Assert.Equal(502, response.StatusCode);
        Assert.Equal("Không thể khôi phục quyền đọc chapter đã mua.", response.Message);
        Assert.Empty(await world.TransactionsAsync(user));
    }

    [Fact]
    public async Task Already_owned_while_ChapterAPI_is_unreachable_returns_503()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await SeedPaymentPurchaseAsync(world, user, chapter, 100m);
        world.Grpc.Unavailable.Add(UnlockChapter);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("ChapterAPI đang tạm thời không khả dụng.", response.Message);
        Assert.False(await world.ChapterUnlockedAsync(user, chapter));
    }

    [Fact]
    public async Task Unknown_chapter_returns_404()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = Guid.NewGuid();
        await world.SeedWalletAsync(user, 500m);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.False(response.Success);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Không tìm thấy chapter.", response.Message);
        await AssertNothingChargedAsync(world, user, chapter, 500m);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Active")] // the entity default
    public async Task Unpublished_chapter_returns_404(string status)
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m, status);
        await world.SeedWalletAsync(user, 500m);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Chapter chưa được phát hành.", response.Message);
        await AssertNothingChargedAsync(world, user, chapter, 500m);
    }

    [Fact]
    public async Task Published_status_is_matched_case_insensitively()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(0m, "PUBLISHED");

        var response = await PurchaseAsync(world, user, chapter);

        Assert.Equal(400, response.StatusCode); // got past the status check, stopped at the free-chapter check
    }

    [Fact]
    public async Task Free_chapter_returns_400()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(0m);
        await world.SeedWalletAsync(user, 500m);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Chapter miễn phí không cần mở khóa.", response.Message);
        await AssertNothingChargedAsync(world, user, chapter, 500m);
    }

    [Fact]
    public async Task ChapterAPI_unreachable_returns_503()
    {
        var world = await _fixture.InMemoryAsync();
        var user = TestWorld.NewUserId();
        var chapter = await world.SeedChapterAsync(120m);
        await world.SeedWalletAsync(user, 500m);
        world.Grpc.Unavailable.Add(GetChapterInfo);

        var response = await PurchaseAsync(world, user, chapter);

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("Không thể lấy thông tin chapter. Vui lòng thử lại sau.", response.Message);
        await AssertNothingChargedAsync(world, user, chapter, 500m);
    }

    [Fact]
    public async Task Purchase_with_a_non_numeric_user_claim_returns_401()
    {
        var world = await _fixture.InMemoryAsync();
        using var scope = world.PaymentScope();

        var result = await Controllers.Payments(scope.ServiceProvider.GetRequiredService<IMediator>(), "abc")
            .PurchaseChapter(new PurchaseChapterRequestDto { ChapterId = Guid.NewGuid() });

        Assert.Equal(401, Controllers.StatusOf(result));
    }
}
