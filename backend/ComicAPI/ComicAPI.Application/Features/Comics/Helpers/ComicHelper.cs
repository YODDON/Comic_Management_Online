using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ComicAPI.Application.DTOs;
using ComicAPI.Domain.Entities;
using ComicAPI.Domain.Interfaces;
using SharedKernel.Contracts.Modules;

namespace ComicAPI.Application.Features.Comics.Helpers
{
    public static class ComicHelper
    {
        public static async Task EnrichOutstandingFlagsAsync(IComicRepository comicRepository, List<ComicSummaryDto> dtos)
        {
            foreach (var dto in dtos)
            {
                dto.IsOutstanding = await comicRepository.IsComicOutstandingAsync(dto.Id);
            }
        }

        public static async Task EnrichComicsWithAuthorNamesAsync(IComicUserLookup userLookup, List<ComicSummaryDto> dtos, List<Comic> comics)
        {
            var comicsWithoutAuthor = comics
                .Where(comic => string.IsNullOrWhiteSpace(comic.Author))
                .ToList();

            foreach (var dto in dtos)
            {
                var comic = comics.First(item => item.Id == dto.Id);
                if (!string.IsNullOrWhiteSpace(comic.Author))
                {
                    dto.AuthorName = comic.Author.Trim();
                }
            }

            var ownerIds = comicsWithoutAuthor.Select(i => i.OwnerId).Distinct().ToList();
            if (!ownerIds.Any()) return;

            try
            {
                var users = await userLookup.GetUsersByIdsAsync(ownerIds);
                var userDict = users.ToDictionary(u => u.Id, u => u.Username);

                foreach (var dto in dtos)
                {
                    var originalComic = comics.First(c => c.Id == dto.Id);
                    if (!string.IsNullOrWhiteSpace(originalComic.Author)) continue;
                    if (userDict.TryGetValue(originalComic.OwnerId, out var username))
                    {
                        dto.AuthorName = username;
                    }
                    else
                    {
                        dto.AuthorName = "Unknown";
                    }
                }
            }
            catch
            {
                foreach (var dto in dtos)
                {
                    var originalComic = comics.First(c => c.Id == dto.Id);
                    if (string.IsNullOrWhiteSpace(originalComic.Author)) dto.AuthorName = "Unknown";
                }
            }
        }

        public static async Task<string> GenerateUniqueSlugAsync(IComicRepository comicRepository, string title, Guid excludedComicId)
        {
            var baseSlug = SharedKernel.Utilities.SlugGenerator.GenerateSlug(title);
            if (string.IsNullOrWhiteSpace(baseSlug))
            {
                baseSlug = $"truyen-{excludedComicId.ToString("N")[..8]}";
            }

            var candidate = baseSlug;
            var suffix = 2;
            while (await comicRepository.SlugExistsAsync(candidate, excludedComicId))
            {
                candidate = $"{baseSlug}-{suffix}";
                suffix++;
            }

            return candidate;
        }
    }
}
