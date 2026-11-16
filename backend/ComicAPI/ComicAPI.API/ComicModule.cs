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
        // The standalone API still uses Redis; the host uses the in-memory cache (T19 finishes the switch).
        services.AddDistributedMemoryCache();

        services.Configure<ComicAPI.Infrastructure.Settings.CloudinarySettings>(configuration.GetSection("Cloudinary"));
        services.AddScoped<ComicAPI.Application.Interfaces.ICloudinaryService, ComicAPI.Infrastructure.Services.CloudinaryService>();

        services.AddAutoMapper(typeof(ComicAPI.Application.Mappings.ComicProfile).Assembly);

        // Still gRPC (replaced in T16-T18). The host does not serve gRPC, so these calls
        // fail at runtime in the host; the handlers catch the error and use a fallback.
        var userApiUrl = Environment.GetEnvironmentVariable("USER_API_URL")
            ?? configuration["GrpcSettings:UserApiUrl"]
            ?? "http://localhost:5054";
        services.AddGrpcClient<UserAPI.Protos.UserService.UserServiceClient>(o =>
        {
            o.Address = new Uri(userApiUrl);
        });

        var chapterApiUrl = Environment.GetEnvironmentVariable("CHAPTER_API_URL")
            ?? configuration["GrpcEndpoints:ChapterAPI"]
            ?? "http://localhost:5115";
        services.AddGrpcClient<ChapterAPI.Protos.ChapterGrpc.ChapterGrpcClient>(o =>
        {
            o.Address = new Uri(chapterApiUrl);
        });

        services.AddScoped<ComicAPI.Domain.Interfaces.ICategoryRepository, ComicAPI.Infrastructure.Repositories.CategoryRepository>();
        services.AddScoped<ComicAPI.Domain.Interfaces.IComicRepository, ComicAPI.Infrastructure.Repositories.ComicRepository>();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(
            typeof(ComicAPI.Application.Features.Comics.Queries.GetComicsQuery).Assembly));

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
