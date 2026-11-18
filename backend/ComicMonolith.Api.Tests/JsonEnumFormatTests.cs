using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BannerAPI;
using ChapterAPI;
using ComicAPI.API;
using MissionAPI;
using PaymentAPI;
using SocialAPI;
using UserAPI.API;
using WalletAPI;
using SharedKernel.Enums;
using SharedKernel.Responses;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// T46: the JSON enum format of every module must be the one its old service used. Only the old WalletAPI
/// added <c>JsonStringEnumConverter</c> (to its own MVC options), so Wallet sends and accepts enums as text,
/// and every other module sends and accepts numbers. In the host all modules share one MVC
/// <see cref="Microsoft.AspNetCore.Mvc.JsonOptions"/>, so a converter added there by one module changes the
/// wire format of all of them (what T09 did by moving WalletAPI's <c>AddJsonOptions</c> into the module).
/// These tests read the options the MVC input and output formatters use, after the same registrations as the
/// host's Program.cs (AddControllers, then the 8 modules), and compare exact JSON.
/// The frontend depends on it: walletService.js TX_STATUS / TX_TYPE are ints (Payment), mission type is an
/// index (Mission), WITHDRAW_STATUS is text (Wallet).
/// </summary>
public class JsonEnumFormatTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static readonly Lazy<JsonSerializerOptions> HostOptions = new(BuildHostJsonOptions);

    private static JsonSerializerOptions BuildHostJsonOptions()
    {
        // Dummy connection strings: AddDbContext only stores them, nothing connects here.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BannerConnection"] = "Server=none;Database=BannerDB",
                ["ConnectionStrings:ChapterConnection"] = "Server=none;Database=ChapterDB",
                ["ConnectionStrings:ComicConnection"] = "Server=none;Database=ComicDB",
                ["ConnectionStrings:MissionConnection"] = "Server=none;Database=MissionDB",
                ["ConnectionStrings:PaymentConnection"] = "Server=none;Database=PaymentDB",
                ["ConnectionStrings:SocialConnection"] = "Server=none;Database=SocialDB",
                ["ConnectionStrings:AuthConnection"] = "Server=none;Database=AuthDB",
                ["ConnectionStrings:WalletConnection"] = "Server=none;Database=WalletDB",
            })
            .Build();

        // Same order as ComicMonolith.Api/Program.cs.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddControllers();
        services.AddBannerModule(configuration);
        services.AddChapterModule(configuration);
        services.AddComicModule(configuration);
        services.AddMissionModule(configuration);
        services.AddPaymentModule(configuration);
        services.AddSocialModule(configuration);
        services.AddUserModule(configuration);
        services.AddWalletModule(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, HostOptions.Value);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, HostOptions.Value)!;

    // ---- Payment: numbers (GET /payments/transactions/{id}) ----

    [Fact]
    public void Payment_transaction_sends_type_and_status_as_numbers()
    {
        var response = new ApiResponse<PaymentAPI.DTOs.TransactionDto>(new PaymentAPI.DTOs.TransactionDto
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId = 7,
            Type = TransactionType.Purchase,
            Amount = 120m,
            CurrencyType = "Coin",
            Status = TransactionStatus.Completed,
            PaymentMethod = "Wallet",
            TransactionCode = "PU1",
            ChapterId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ComicId = null,
            ChapterTitle = "Ch 1",
            ChapterNumber = 1,
            ChapterSlug = "ch-1",
            CreatedAt = CreatedAt,
        });

        Assert.Equal(
            "{\"success\":true,\"message\":null,\"data\":{"
            + "\"id\":\"11111111-1111-1111-1111-111111111111\",\"userId\":7,\"type\":0,\"amount\":120,"
            + "\"currencyType\":\"Coin\",\"status\":1,\"paymentMethod\":\"Wallet\",\"transactionCode\":\"PU1\","
            + "\"chapterId\":\"22222222-2222-2222-2222-222222222222\",\"comicId\":null,\"chapterTitle\":\"Ch 1\","
            + "\"chapterNumber\":1,\"chapterSlug\":\"ch-1\",\"createdAt\":\"2026-01-02T03:04:05Z\"},"
            + "\"statusCode\":200,\"errors\":[]}",
            Serialize(response));
    }

    // ---- Mission: numbers (GET /missions/my-missions) ----

    [Fact]
    public void Mission_response_sends_type_as_a_number()
    {
        IEnumerable<MissionAPI.DTOs.UserMissionDto> missions = new List<MissionAPI.DTOs.UserMissionDto>
        {
            new()
            {
                MissionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Title = "Buy 1 chapter",
                Description = "d",
                RewardCoin = 5m,
                Type = MissionType.PurchaseChapter,
                TargetCount = 1,
                CurrentProgress = 0,
                IsCompleted = false,
                CompletedAt = null,
            },
        };

        Assert.Equal(
            "{\"success\":true,\"message\":null,\"data\":[{"
            + "\"missionId\":\"33333333-3333-3333-3333-333333333333\",\"title\":\"Buy 1 chapter\",\"description\":\"d\","
            + "\"rewardCoin\":5,\"type\":1,\"targetCount\":1,\"currentProgress\":0,\"isCompleted\":false,\"completedAt\":null}],"
            + "\"statusCode\":200,\"errors\":[]}",
            Serialize(new ApiResponse<IEnumerable<MissionAPI.DTOs.UserMissionDto>>(missions)));
    }

    [Fact]
    public void Mission_request_with_a_text_enum_is_rejected_as_the_old_MissionAPI_did()
    {
        Assert.Throws<JsonException>(() => Deserialize<MissionAPI.DTOs.CreateMissionDto>("{\"title\":\"t\",\"type\":\"ReadChapter\"}"));
        Assert.Equal(MissionType.LeaveComment, Deserialize<MissionAPI.DTOs.CreateMissionDto>("{\"title\":\"t\",\"type\":2}").Type);
    }

    // ---- Wallet: text (GET /currency/history, GET /withdraws/me, PUT /withdraws/{id}, POST /currency) ----

    [Fact]
    public void Wallet_ledger_entry_sends_type_as_text()
    {
        var history = new WalletAPI.DTOs.CurrencyHistoryResultDto(
            new List<WalletAPI.DTOs.CurrencyEntryDto>
            {
                new()
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Amount = -120m,
                    Type = TransactionType.Purchase,
                    Description = "Unlock",
                    ReferenceId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    CreatedAt = CreatedAt,
                },
            },
            totalCount: 1, page: 1, pageSize: 10, balance: 380m);

        Assert.Equal(
            "{\"success\":true,\"message\":null,\"data\":{\"balance\":380,\"items\":[{"
            + "\"id\":\"44444444-4444-4444-4444-444444444444\",\"amount\":-120,\"type\":\"Purchase\",\"description\":\"Unlock\","
            + "\"referenceId\":\"55555555-5555-5555-5555-555555555555\",\"createdAt\":\"2026-01-02T03:04:05Z\"}],"
            + "\"totalCount\":1,\"page\":1,\"pageSize\":10,\"totalPages\":1},"
            + "\"statusCode\":200,\"errors\":[]}",
            Serialize(new ApiResponse<WalletAPI.DTOs.CurrencyHistoryResultDto>(history)));
    }

    [Fact]
    public void Wallet_withdraw_sends_status_as_text()
    {
        var withdraws = new PagedResult<WalletAPI.DTOs.WithdrawDto>(
            new List<WalletAPI.DTOs.WithdrawDto>
            {
                new()
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    UserId = 7,
                    Amount = 100000m,
                    Fee = 5000m,
                    TotalDeducted = 105000m,
                    BankAccount = "0123",
                    BankName = "Bank",
                    AccountName = "NGUYEN VAN A",
                    Status = WithdrawStatus.Pending,
                    Note = null,
                    CreatedAt = CreatedAt,
                    ProcessedAt = null,
                    Balance = null,
                },
            },
            totalCount: 1, page: 1, pageSize: 10);

        Assert.Equal(
            "{\"success\":true,\"message\":null,\"data\":{\"items\":[{"
            + "\"id\":\"66666666-6666-6666-6666-666666666666\",\"userId\":7,\"amount\":100000,\"fee\":5000,"
            + "\"totalDeducted\":105000,\"bankAccount\":\"0123\",\"bankName\":\"Bank\",\"accountName\":\"NGUYEN VAN A\","
            + "\"status\":\"Pending\",\"note\":null,\"createdAt\":\"2026-01-02T03:04:05Z\",\"processedAt\":null,\"balance\":null}],"
            + "\"totalCount\":1,\"page\":1,\"pageSize\":10,\"totalPages\":1},"
            + "\"statusCode\":200,\"errors\":[]}",
            Serialize(new ApiResponse<PagedResult<WalletAPI.DTOs.WithdrawDto>>(withdraws)));
    }

    [Fact]
    public void Wallet_withdraw_update_accepts_status_as_text_and_as_a_number()
    {
        // AdminWithdrawalsPage: updateWithdrawStatus(item.id, WITHDRAW_STATUS.REJECTED, note) sends text.
        var text = Deserialize<WalletAPI.DTOs.UpdateWithdrawStatusRequestDto>("{\"status\":\"Rejected\",\"note\":\"x\"}");
        Assert.Equal(WithdrawStatus.Rejected, text.Status);
        Assert.Equal("x", text.Note);

        // The old converter also read numbers (JsonStringEnumConverter allows integers by default).
        Assert.Equal(WithdrawStatus.Approved, Deserialize<WalletAPI.DTOs.UpdateWithdrawStatusRequestDto>("{\"status\":1}").Status);
    }

    [Fact]
    public void Wallet_currency_entry_request_accepts_type_as_text()
    {
        var request = Deserialize<WalletAPI.DTOs.CreateCurrencyEntryRequestDto>(
            "{\"userId\":7,\"amount\":50,\"type\":\"ManualTopUp\",\"description\":\"d\"}");

        Assert.Equal(TransactionType.ManualTopUp, request.Type);
    }
}
