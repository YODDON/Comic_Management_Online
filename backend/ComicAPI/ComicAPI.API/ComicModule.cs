using ComicAPI.API.Controllers;
using ComicAPI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ComicAPI.API;

public static class ComicModule
{
    public static IServiceCollection AddComicModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(ComicsController).Assembly);

        var comicConn = Environment.GetEnvironmentVariable("COMIC_DB_CONNECTION")
            ?? configuration.GetConnectionString("ComicConnection");
        if (!string.IsNullOrEmpty(comicConn))
        {
            services.AddDbContext<ComicDbContext>(options => options.UseSqlServer(comicConn));
        }

        // Used by GetHotComics, GetOutstandingComicsList and GetLastCompletedComics (IDistributedCache).
        // In-memory cache for both the host and the standalone API (Redis removed in T19).
        services.AddDistributedMemoryCache();

        services.Configure<ComicAPI.Infrastructure.Settings.CloudinarySettings>(configuration.GetSection("Cloudinary"));
        services.AddScoped<ComicAPI.Application.Interfaces.ICloudinaryService, ComicAPI.Infrastructure.Services.CloudinaryService>();

        // IComicUserLookup and IComicChapterLookup (author names, chapter counts, purchased comics) are
        // registered by AddUserModule and AddChapterModule (T17).

        services.AddScoped<ComicAPI.Domain.Interfaces.ICategoryRepository, ComicAPI.Infrastructure.Repositories.CategoryRepository>();
        services.AddScoped<ComicAPI.Domain.Interfaces.IComicRepository, ComicAPI.Infrastructure.Repositories.ComicRepository>();

        // Not registered here (no bus in the host until T20): ComicViewedEventConsumer and MassTransit.
        // Not mapped: the comic.proto server (no service implementation exists in ComicAPI.API).

        return services;
    }

    public static WebApplication UseComicModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<ComicDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
