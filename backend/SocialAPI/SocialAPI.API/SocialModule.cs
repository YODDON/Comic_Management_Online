using Microsoft.EntityFrameworkCore;
using SocialAPI.API.Controllers;
using SocialAPI.Data;
using SocialAPI.Interfaces;
using SocialAPI.Repositories;
using SocialAPI.Services;

namespace SocialAPI;

public static class SocialModule
{
    public static IServiceCollection AddSocialModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(CommentsController).Assembly);

        var socialConn = Environment.GetEnvironmentVariable("SOCIAL_DB_CONNECTION")
            ?? configuration.GetConnectionString("SocialConnection");
        if (!string.IsNullOrEmpty(socialConn))
        {
            services.AddDbContext<SocialDbContext>(options => options.UseSqlServer(socialConn));
        }

        // Still an HTTP call to /comics/{id} through API_GATEWAY_URL (same as the standalone API).
        // In the host this only works once the T13 path-rewrite middleware maps /comics to /api/comics.
        var apiGatewayUrl = Environment.GetEnvironmentVariable("API_GATEWAY_URL");
        if (string.IsNullOrWhiteSpace(apiGatewayUrl))
        {
            apiGatewayUrl = configuration["ApiGateway:BaseUrl"];
        }
        if (string.IsNullOrWhiteSpace(apiGatewayUrl))
        {
            apiGatewayUrl = "http://127.0.0.1:5028";
        }
        // Explicit client name: the default name is the type name without namespace ("IComicValidator"),
        // which clashes with ChapterAPI's IComicValidator once both modules are in the same host.
        services.AddHttpClient<IComicValidator, ComicValidator>("SocialAPI.ComicValidator", client =>
        {
            client.BaseAddress = new Uri(apiGatewayUrl);
        });

        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IFavoriteRepository, FavoriteRepository>();
        services.AddScoped<IFollowRepository, FollowRepository>();
        services.AddScoped<IReadingHistoryRepository, ReadingHistoryRepository>();
        services.AddScoped<ISocialActivityRepository, SocialActivityRepository>();
        services.AddScoped<IMissionProgressNotifier, MissionProgressNotifier>();

        // In-process replacement for the gRPC call MissionAPI -> SocialAPI (T17).
        services.AddScoped<SharedKernel.Contracts.Modules.IMissionSocialActivity, SocialAPI.API.ModuleServices.MissionSocialActivity>();

        // Not registered here: MassTransit (the host owns the single bus, see Program.cs). Social has
        // no consumers, it only publishes. The host bus has no EF Outbox until T22 decides (T20).

        return services;
    }

    public static WebApplication UseSocialModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<SocialDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
