using ChapterAPI.Application.Consumers;
using ChapterAPI.Controllers;
using ChapterAPI.Data;
using ChapterAPI.Interfaces;
using ChapterAPI.Repositories;
using ChapterAPI.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace ChapterAPI;

public static class ChapterModule
{
    public static IServiceCollection AddChapterModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(ChaptersController).Assembly);

        var chapterConn = Environment.GetEnvironmentVariable("CHAPTER_DB_CONNECTION")
            ?? configuration.GetConnectionString("ChapterConnection");
        if (!string.IsNullOrEmpty(chapterConn))
        {
            services.AddDbContext<ChapterDbContext>(options => options.UseSqlServer(chapterConn));
        }

        services.Configure<ChapterAPI.Settings.CloudinarySettings>(configuration.GetSection("Cloudinary"));
        services.AddScoped<ChapterAPI.Interfaces.ICloudinaryService, ChapterAPI.Services.CloudinaryService>();

        // Still an HTTP call to /comics/{id} through API_GATEWAY_URL (same as the standalone API).
        // In the host this only works once the T13 path-rewrite middleware maps /comics to /api/comics.
        var apiGatewayUrl = Environment.GetEnvironmentVariable("API_GATEWAY_URL")
            ?? configuration["ApiGateway:BaseUrl"]
            ?? "http://localhost:5028";
        services.AddHttpClient<IComicValidator, ComicValidator>(client =>
        {
            client.BaseAddress = new Uri(apiGatewayUrl + "/api");
        });

        services.AddScoped<IChapterRepository, ChapterRepository>();
        services.AddScoped<IMissionProgressNotifier, MissionProgressNotifier>();

        // In-process replacement for the gRPC calls ComicAPI -> ChapterAPI (T17).
        services.AddScoped<SharedKernel.Contracts.Modules.IComicChapterLookup, ChapterAPI.ModuleServices.ComicChapterLookup>();
        // In-process replacement for the gRPC call MissionAPI -> ChapterAPI (T17).
        services.AddScoped<SharedKernel.Contracts.Modules.IMissionChapterActivity, ChapterAPI.ModuleServices.MissionChapterActivity>();
        // In-process replacement for the gRPC calls PaymentAPI -> ChapterAPI (T17).
        services.AddScoped<SharedKernel.Contracts.Modules.IPaymentChapterGateway, ChapterAPI.ModuleServices.PaymentChapterGateway>();

        // Not registered here: MassTransit. The host owns the single bus and calls AddChapterConsumers /
        // ConfigureChapterEndpoints below (T20). The host bus has no EF Outbox until T22 decides.

        return services;
    }

    /// <summary>Consumers of this module on the host's single bus (T20). Same as ChapterAPI's Program.cs, without the EF Outbox.</summary>
    public static IBusRegistrationConfigurator AddChapterConsumers(this IBusRegistrationConfigurator bus)
    {
        bus.AddConsumer<UnlockChapterConsumer>();
        return bus;
    }

    /// <summary>Receive endpoints of this module (T20). Queue names unchanged from ChapterAPI's Program.cs.</summary>
    public static void ConfigureChapterEndpoints(this IBusFactoryConfigurator cfg, IBusRegistrationContext context)
    {
        cfg.ReceiveEndpoint("unlock-chapter", e => e.ConfigureConsumer<UnlockChapterConsumer>(context));
    }

    public static WebApplication UseChapterModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<ChapterDbContext>();
        db?.Database.Migrate();

        return app;
    }
}
