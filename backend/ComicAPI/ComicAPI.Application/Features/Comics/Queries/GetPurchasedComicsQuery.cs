using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using ComicAPI.Application.DTOs;
using ComicAPI.Application.Features.Comics.Helpers;
using ComicAPI.Domain.Interfaces;
using MediatR;
using SharedKernel.Responses;
using SharedKernel.Contracts.Modules;

namespace ComicAPI.Application.Features.Comics.Queries
{
    public class GetPurchasedComicsQuery : IRequest<ApiResponse<PagedResult<ComicSummaryDto>>>
    {
        public int UserId { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
    }

    public class GetPurchasedComicsQueryHandler : IRequestHandler<GetPurchasedComicsQuery, ApiResponse<PagedResult<ComicSummaryDto>>>
    {
        private readonly IComicRepository _comicRepository;
        private readonly IMapper _mapper;
        private readonly IComicUserLookup _userLookup;
        private readonly IComicChapterLookup _chapterLookup;

        public GetPurchasedComicsQueryHandler(IComicRepository comicRepository, IMapper mapper, IComicUserLookup userLookup, IComicChapterLookup chapterLookup)
        {
            _comicRepository = comicRepository;
            _mapper = mapper;
            _userLookup = userLookup;
            _chapterLookup = chapterLookup;
        }

        public async Task<ApiResponse<PagedResult<ComicSummaryDto>>> Handle(GetPurchasedComicsQuery request, CancellationToken cancellationToken)
        {
            var pageNumber = request.PageNumber < 1 ? 1 : request.PageNumber;
            var pageSize = request.PageSize < 1 ? 10 : (request.PageSize > 100 ? 100 : request.PageSize);

            List<Guid> purchasedComicIds;
            try
            {
                var response = await _chapterLookup.GetPurchasedComicIdsAsync(request.UserId, cancellationToken);
                purchasedComicIds = response
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();
            }
            catch
            {
                return ApiResponse<PagedResult<ComicSummaryDto>>.ErrorResponse(
                    "Could not retrieve purchased comics from ChapterAPI.",
                    503);
            }

            if (!purchasedComicIds.Any())
            {
                var emptyResult = new PagedResult<ComicSummaryDto>(new List<ComicSummaryDto>(), 0, pageNumber, pageSize);
                return new ApiResponse<PagedResult<ComicSummaryDto>>(emptyResult, "Purchased comics retrieved.", 200);
            }

            var (comics, totalCount) = await _comicRepository.GetComicsByIdsAsync(purchasedComicIds, pageNumber, pageSize);
            var dtos = _mapper.Map<List<ComicSummaryDto>>(comics);
            await ComicHelper.EnrichComicsWithAuthorNamesAsync(_userLookup, dtos, comics);

            var pagedResult = new PagedResult<ComicSummaryDto>(dtos, totalCount, pageNumber, pageSize);
            return new ApiResponse<PagedResult<ComicSummaryDto>>(pagedResult, "Purchased comics retrieved.", 200);
        }
    }
}
