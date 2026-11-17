using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using ComicAPI.Application.DTOs;
using ComicAPI.Domain.Interfaces;
using MediatR;
using SharedKernel.Responses;
using SharedKernel.Contracts.Modules;

namespace ComicAPI.Application.Features.Comics.Queries
{
    public class GetComicBySlugQuery : IRequest<ApiResponse<ComicDetailDto>>
    {
        public string Slug { get; set; } = null!;
    }

    public class GetComicBySlugQueryHandler : IRequestHandler<GetComicBySlugQuery, ApiResponse<ComicDetailDto>>
    {
        private readonly IComicRepository _comicRepository;
        private readonly IMapper _mapper;
        private readonly IComicUserLookup _userLookup;
        private readonly IComicChapterLookup _chapterLookup;

        public GetComicBySlugQueryHandler(IComicRepository comicRepository, IMapper mapper, IComicUserLookup userLookup, IComicChapterLookup chapterLookup)
        {
            _comicRepository = comicRepository;
            _mapper = mapper;
            _userLookup = userLookup;
            _chapterLookup = chapterLookup;
        }

        public async Task<ApiResponse<ComicDetailDto>> Handle(GetComicBySlugQuery request, CancellationToken cancellationToken)
        {
            var comic = await _comicRepository.GetComicBySlugAsync(request.Slug);
            if (comic == null)
            {
                return new ApiResponse<ComicDetailDto>(null, "Comic not found.", 404);
            }

            var dto = _mapper.Map<ComicDetailDto>(comic);
            dto.IsOutstanding = await _comicRepository.IsComicOutstandingAsync(comic.Id);

            if (!string.IsNullOrWhiteSpace(comic.Author))
            {
                dto.AuthorName = comic.Author.Trim();
            }
            else
            {
                try
                {
                    var users = await _userLookup.GetUsersByIdsAsync(new[] { comic.OwnerId }, cancellationToken);
                    var user = users.FirstOrDefault();
                    dto.AuthorName = user != null ? user.Username : "Unknown";
                }
                catch
                {
                    dto.AuthorName = "Unknown";
                }
            }

            try
            {
                dto.ChapterCount = await _chapterLookup.GetChapterCountAsync(comic.Id, cancellationToken);
            }
            catch
            {
                dto.ChapterCount = 0;
            }

            return new ApiResponse<ComicDetailDto>(dto, "Comic retrieved successfully.", 200);
        }
    }
}
