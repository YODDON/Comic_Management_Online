using MediatR;
using SharedKernel.Contracts.Modules;
using UserAPI.Application.Features.UserQueries.Queries;

namespace UserAPI.API.ModuleServices;

/// <summary>
/// In-process replacement for the gRPC call <c>UserService.GetUsersByIds</c> (same query and same field mapping as
/// <c>UserGrpcService.GetUsersByIds</c>). Any failure other than cancellation is wrapped in <see cref="ModuleCallException"/>.
/// </summary>
public sealed class ComicUserLookup : IComicUserLookup
{
    private readonly IMediator _mediator;

    public ComicUserLookup(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task<IReadOnlyList<UserSummary>> GetUsersByIdsAsync(
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var users = await _mediator.Send(new GetUsersByIdsQuery { Ids = userIds }, cancellationToken);
            return users
                .Select(user => new UserSummary(
                    user.Id,
                    user.Username,
                    user.Email,
                    user.AvatarUrl ?? string.Empty,
                    user.IsActive,
                    user.UserRoles.Select(ur => ur.Role.Name).ToList()))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModuleCallException("UserAPI could not return the users.", ex);
        }
    }
}
