using ChapterAPI.Application.Features.Chapters.Queries;
using MediatR;
using SharedKernel.Contracts.Modules;

namespace ChapterAPI.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC calls <c>ChapterGrpc.GetChapterCount</c> and <c>GetPurchasedComicIds</c>
/// (same queries and same validation as <c>ChapterGrpcService</c>). Any failure other than cancellation is wrapped
/// in <see cref="ModuleCallException"/>.
/// </summary>
public sealed class ComicChapterLookup : IComicChapterLookup
{
    private readonly IMediator _mediator;

    public ComicChapterLookup(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task<int> GetChapterCountAsync(Guid comicId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _mediator.Send(new GetChapterCountQuery { ComicId = comicId }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("ChapterAPI could not return the chapter count.", ex);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetPurchasedComicIdsAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ModuleCallException("UserId is invalid.");
        }

        try
        {
            return await _mediator.Send(new GetPurchasedComicIdsQuery { UserId = userId }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("ChapterAPI could not return the purchased comics.", ex);
        }
    }
}
