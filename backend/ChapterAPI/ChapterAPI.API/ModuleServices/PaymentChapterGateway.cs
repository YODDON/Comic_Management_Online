using ChapterAPI.Application.Features.Chapters.Commands;
using ChapterAPI.Application.Features.Chapters.Queries;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Contracts.Modules;

namespace ChapterAPI.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC calls <c>ChapterGrpc.GetChapterInfo</c> and <c>ChapterGrpc.UnlockChapter</c>
/// (same queries and commands, same not-found answer as <c>ChapterGrpcService</c>). Any failure other than
/// cancellation is wrapped in <see cref="ModuleCallException"/>.
/// Every call runs in its own DI scope, like a gRPC request did: Payment calls <c>GetChapterInfo</c> from
/// <c>Task.WhenAll</c> (history enrichment), and one scope would share one <c>ChapterDbContext</c> between those
/// concurrent calls, which EF does not allow. A separate scope also keeps the Chapter unit of work (DbContext,
/// change tracker, any open transaction) apart from the caller's.
/// </summary>
public sealed class PaymentChapterGateway : IPaymentChapterGateway
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentChapterGateway(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ChapterInfo> GetChapterInfoAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var chapter = await mediator.Send(new GetChapterInfoQuery { ChapterId = chapterId }, cancellationToken);
            if (chapter == null)
            {
                return ChapterInfo.NotFound;
            }

            return new ChapterInfo(
                Exists: true,
                ComicId: chapter.ComicId,
                UnitPrice: chapter.UnitPrice,
                Title: chapter.Title,
                ChapterNumber: chapter.ChapterNumber,
                Slug: chapter.Slug,
                Status: chapter.Status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("ChapterAPI could not return the chapter info.", ex);
        }
    }

    public async Task<UnlockChapterResult> UnlockChapterAsync(
        int userId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new UnlockChapterCommand { UserId = userId, ChapterId = chapterId }, cancellationToken);
            return new UnlockChapterResult(result.Success, result.AlreadyPurchased);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("ChapterAPI could not unlock the chapter.", ex);
        }
    }
}
